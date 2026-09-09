using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Mediapipe;
using Mediapipe.Tasks.Core.Proto;
using Mediapipe.Tasks.Vision.FaceGeometry;
using FaceGeometryProto = Mediapipe.Tasks.Vision.FaceGeometry.Proto.FaceGeometry;
using UnityEditor;

public static class P3FaceGeometryDiagnostic
{
    private const string TaskName =
        "KiwiCodexTask_P3A_FaceGeometry_20260909-162449JST";

    private const string ExpectedManifestSha =
        "C7324CF1DA2FC267C8E14BEB6ADF8ECEE73846E54C608B330EB2A9FC629FEBC4";

    private const string PackageName =
        "com.github.homuler.mediapipe";

    private const string BundleRelativePath =
        "PackageResources/MediaPipe/face_landmarker_v2_with_blendshapes.bytes";

    private const string MetadataEntryName =
        "geometry_pipeline_metadata_landmarks.binarypb";

    private const string ExpectedMetadataSha =
        "BDBCDA96DFCB7DA883DA124AAA2C55DEE49770D934F0FCC71747F8C21BDC75B4";

    private const int RepeatCount = 5;
    private const double VFoVDegrees = 63.0;
    private const double Near = 1.0;
    private const double Far = 10000.0;

    private sealed class Metadata
    {
        public readonly List<float> VertexBuffer = new List<float>();
        public readonly List<uint> IndexBuffer = new List<uint>();
        public readonly List<WeightedRef> Weights = new List<WeightedRef>();
        public int InputSource;
        public int VertexType;
        public int PrimitiveType;
    }

    private struct WeightedRef
    {
        public uint LandmarkId;
        public float Weight;
    }

    private sealed class Fixture
    {
        public string Name;
        public int Width;
        public int Height;
        public NormalizedLandmarkList Landmarks;
    }

    private sealed class Evaluation
    {
        public string Name;
        public int Width;
        public int Height;
        public byte[] Serialized;
        public float[,] Pose;
        public float[] MeshPositions;
        public int MeshVertexCount;
        public bool Finite;
        public double LastRowMaxError;
        public double ScaleRelativeSpread;
        public double OrthogonalityMaxError;
        public double RotationDeterminant;
        public double ReprojectionRms;
        public double ReprojectionMax;
        public int RuntimeNegativeZCount;
    }

    public static void RunBatch()
    {
        int exitCode = 1;
        string projectRoot = Directory.GetCurrentDirectory();
        string taskRoot = Path.Combine(projectRoot, "CodexTasks", TaskName);
        string resultPath = Path.Combine(taskRoot, "P3_DETERMINISTIC_DIAGNOSTIC_RESULT.txt");

        try
        {
            Directory.CreateDirectory(taskRoot);
            string manifestPath = Path.Combine(
                taskRoot,
                "P3_TEST_MANIFEST_PRE_EXECUTION.txt");

            Require(
                Sha256Hex(File.ReadAllBytes(manifestPath)) == ExpectedManifestSha,
                "Pre-execution manifest SHA mismatch.");

            UnityEditor.PackageManager.PackageInfo package =
                UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                    .Single(p => p.name == PackageName);

            string bundlePath = Path.Combine(package.resolvedPath, BundleRelativePath);
            byte[] metadataBytes = ReadZipEntry(bundlePath, MetadataEntryName);
            string metadataSha = Sha256Hex(metadataBytes);
            Require(metadataSha == ExpectedMetadataSha, "Metadata SHA mismatch.");

            Metadata metadata = ParseMetadata(metadataBytes);
            Require(metadata.VertexType == 0, "Metadata vertex type is not VERTEX_PT.");
            Require(metadata.VertexBuffer.Count % 5 == 0, "Invalid vertex buffer size.");
            int canonicalVertexCount = metadata.VertexBuffer.Count / 5;
            Require(canonicalVertexCount == 468, "Canonical vertex count is not 468.");
            Require(metadata.Weights.Count > 0, "Procrustes basis is empty.");

            string basisIdentity = ComputeBasisIdentity(metadata.Weights);
            List<Fixture> fixtures = BuildFixtures(metadata);
            CalculatorGraphConfig config = BuildGraphConfig(metadataBytes);

            var results = new List<Evaluation>();
            var deterministicFailures = new List<string>();

            using (var graph = new CalculatorGraph(config))
            using (OutputStreamPoller<FaceGeometryProto> poller =
                graph.AddOutputStreamPoller<FaceGeometryProto>("geometry"))
            {
                graph.StartRun();
                long timestamp = 1000;

                foreach (Fixture fixture in fixtures)
                {
                    byte[] firstBytes = null;
                    for (int repeat = 0; repeat < RepeatCount; ++repeat)
                    {
                        FaceGeometryProto geometry = Execute(
                            graph,
                            poller,
                            fixture,
                            timestamp++);

                        Evaluation evaluation = Evaluate(fixture, geometry);
                        results.Add(evaluation);

                        if (repeat == 0)
                        {
                            firstBytes = evaluation.Serialized;
                        }
                        else if (!firstBytes.SequenceEqual(evaluation.Serialized))
                        {
                            deterministicFailures.Add(
                                fixture.Name + ":repeat=" + repeat);
                        }
                    }
                }

                graph.CloseAllPacketSources();
                graph.WaitUntilDone();
            }

            Evaluation primary = results.First(r => r.Name == "base_16_9");
            Evaluation equivalent = results.First(r => r.Name == "base_16_9_absolute_equivalent");
            double aspectPoseMax = MaxAbsDifference(primary.Pose, equivalent.Pose);
            double aspectMeshMax = MaxAbsDifference(
                primary.MeshPositions,
                equivalent.MeshPositions);

            bool outputPass = results.All(r => r.MeshVertexCount == 468 && r.Finite);
            bool posePass = results.All(r =>
                r.LastRowMaxError <= 1e-6 &&
                r.ScaleRelativeSpread <= 1e-5 &&
                r.OrthogonalityMaxError <= 2e-5 &&
                r.RotationDeterminant >= 0.99998);
            bool reprojectionPass = results.All(r =>
                r.ReprojectionRms <= 1e-5 &&
                r.ReprojectionMax <= 5e-5);
            bool determinismPass = deterministicFailures.Count == 0;
            bool aspectPass =
                aspectPoseMax <= 5e-6 &&
                aspectMeshMax <= 5e-6;
            bool handednessPass = results.All(r =>
                r.RotationDeterminant > 0.0 &&
                r.RuntimeNegativeZCount == 468);
            bool overallPass =
                outputPass &&
                posePass &&
                reprojectionPass &&
                determinismPass &&
                aspectPass &&
                handednessPass;

            var report = new StringBuilder();
            report.AppendLine("P3A installed FaceGeometry deterministic diagnostic result");
            report.AppendLine("EXECUTION_KIND=DETERMINISTIC_DIAGNOSTIC_EXECUTION");
            report.AppendLine("P3_TEST_MANIFEST_SHA_PRE_EXECUTION=" + ExpectedManifestSha);
            report.AppendLine("INSTALLED_PACKAGE_RESOLVED_PATH=" + package.resolvedPath);
            report.AppendLine("FACEGEOMETRY_METADATA_FILE=" + bundlePath + "!" + MetadataEntryName);
            report.AppendLine("FACEGEOMETRY_METADATA_SHA=" + metadataSha);
            report.AppendLine("FACEGEOMETRY_METADATA_INPUT_SOURCE=EXACT_BUNDLE_ENTRY_EMBEDDED_AS_EXTERNALFILE_FILE_CONTENT");
            report.AppendLine("FACEGEOMETRY_CANONICAL_VERTEX_COUNT=" + canonicalVertexCount);
            report.AppendLine("FACEGEOMETRY_CANONICAL_UNIT=CENTIMETER");
            report.AppendLine("FACEGEOMETRY_INDEX_COUNT=" + metadata.IndexBuffer.Count);
            report.AppendLine("FACEGEOMETRY_PROCRUSTES_BASIS_COUNT=" + metadata.Weights.Count);
            report.AppendLine("FACEGEOMETRY_PROCRUSTES_BASIS_IDENTITY=" + basisIdentity);
            report.AppendLine("FACEGEOMETRY_METADATA_INPUT_SOURCE_ENUM=" + metadata.InputSource);
            report.AppendLine("INSTALLED_GRAPH_INITIALIZATION=PASS");
            report.AppendLine("ARBITRARY_SINGLE_NORMALIZEDLANDMARKLIST_INPUT=PASS");
            report.AppendLine("EXPLICIT_IMAGE_SIZE_VIA_IMAGE_PROPERTIES_CALCULATOR=PASS");
            report.AppendLine("SECOND_FACE_INFERENCE=NONE");
            report.AppendLine("TEMPORAL_STATE=NONE");
            report.AppendLine("REPEAT_COUNT=" + RepeatCount);
            report.AppendLine("OUTPUT_MESH_SEMANTIC=" + (outputPass ? "PASS" : "FAIL"));
            report.AppendLine("POSE_MATRIX_SEMANTIC=" + (posePass ? "PASS" : "FAIL"));
            report.AppendLine("REPROJECTION_CONTRACT=" + (reprojectionPass ? "PASS" : "FAIL"));
            report.AppendLine("DETERMINISM=" + (determinismPass ? "PASS" : "FAIL"));
            report.AppendLine("HANDEDNESS_SEMANTIC=" + (handednessPass ? "PASS" : "FAIL"));
            report.AppendLine("ASPECT_EQUIVALENCE=" + (aspectPass ? "PASS" : "FAIL"));
            report.AppendLine("ASPECT_EQUIVALENT_POSE_ABS_MAX=" + F(aspectPoseMax));
            report.AppendLine("ASPECT_EQUIVALENT_MESH_XYZ_ABS_MAX=" + F(aspectMeshMax));
            report.AppendLine("DETERMINISM_FAILURES=" +
                (deterministicFailures.Count == 0
                    ? "NONE"
                    : string.Join(",", deterministicFailures)));

            foreach (Evaluation evaluation in results.Where((_, index) => index % RepeatCount == 0))
            {
                report.AppendLine();
                report.AppendLine("FIXTURE=" + evaluation.Name);
                report.AppendLine("IMAGE_SIZE=" + evaluation.Width + "x" + evaluation.Height);
                report.AppendLine("OUTPUT_SHA256=" + Sha256Hex(evaluation.Serialized));
                report.AppendLine("MESH_VERTEX_COUNT=" + evaluation.MeshVertexCount);
                report.AppendLine("FINITE=" + evaluation.Finite);
                report.AppendLine("POSE_LAST_ROW_ABS_MAX_ERROR=" + F(evaluation.LastRowMaxError));
                report.AppendLine("POSE_COLUMN_SCALE_RELATIVE_SPREAD=" + F(evaluation.ScaleRelativeSpread));
                report.AppendLine("POSE_ROTATION_ORTHOGONALITY_ABS_MAX_ERROR=" + F(evaluation.OrthogonalityMaxError));
                report.AppendLine("POSE_ROTATION_DETERMINANT=" + F(evaluation.RotationDeterminant));
                report.AppendLine("RUNTIME_NEGATIVE_Z_COUNT=" + evaluation.RuntimeNegativeZCount);
                report.AppendLine("REPROJECTION_XY_RMS=" + F(evaluation.ReprojectionRms));
                report.AppendLine("REPROJECTION_XY_MAX=" + F(evaluation.ReprojectionMax));
            }

            report.AppendLine();
            report.AppendLine("GEOMETRY_PIPELINE_DETERMINISTIC_SELFTEST=" +
                (overallPass ? "PASS" : "FAIL"));

            File.WriteAllText(resultPath, report.ToString(), new UTF8Encoding(false));
            UnityEngine.Debug.Log(report.ToString());
            exitCode = overallPass ? 0 : 2;
        }
        catch (Exception exception)
        {
            Directory.CreateDirectory(taskRoot);
            File.WriteAllText(
                resultPath,
                "GEOMETRY_PIPELINE_DETERMINISTIC_SELFTEST=ERROR\n" + exception,
                new UTF8Encoding(false));
            UnityEngine.Debug.LogException(exception);
            exitCode = 1;
        }
        finally
        {
            EditorApplication.Exit(exitCode);
        }
    }

    private static FaceGeometryProto Execute(
        CalculatorGraph graph,
        OutputStreamPoller<FaceGeometryProto> poller,
        Fixture fixture,
        long timestamp)
    {
        var frame = new ImageFrame(
            ImageFormat.Types.Format.Srgba,
            fixture.Width,
            fixture.Height);
        frame.SetToZero();

        Packet<ImageFrame> imagePacket = Packet.CreateImageFrameAt(frame, timestamp);
        Packet<NormalizedLandmarkList> landmarkPacket =
            Packet.CreateProtoAt(fixture.Landmarks, timestamp);

        graph.AddPacketToInputStream("landmarks", landmarkPacket);
        graph.AddPacketToInputStream("image", imagePacket);
        graph.WaitUntilIdle();

        using (var outputPacket = new Packet<FaceGeometryProto>())
        {
            Require(poller.Next(outputPacket), "Expected FaceGeometry output is absent.");
            Require(!outputPacket.IsEmpty(), "FaceGeometry output packet is empty.");
            return outputPacket.GetProto(FaceGeometryProto.Parser);
        }
    }

    private static CalculatorGraphConfig BuildGraphConfig(byte[] metadataBytes)
    {
        var config = new CalculatorGraphConfig { NumThreads = 1 };
        config.InputStream.Add("landmarks");
        config.InputStream.Add("image");
        config.OutputStream.Add("geometry");

        var imageProperties = new CalculatorGraphConfig.Types.Node
        {
            Calculator = "ImagePropertiesCalculator"
        };
        imageProperties.InputStream.Add("IMAGE:image");
        imageProperties.OutputStream.Add("SIZE:image_size");
        config.Node.Add(imageProperties);

        var environment = new CalculatorGraphConfig.Types.Node
        {
            Calculator =
                "mediapipe.tasks.vision.face_geometry." +
                "FaceGeometryEnvGeneratorCalculator",
            Options = CalculatorOptions.Parser.ParseFrom(
                WrapExtension(512499201u, BuildEnvironmentGeneratorOptions()))
        };
        environment.OutputSidePacket.Add("ENVIRONMENT:environment");
        config.Node.Add(environment);

        var geometryOptions = new FaceGeometryPipelineCalculatorOptions
        {
            MetadataFile = new ExternalFile
            {
                FileContent = ByteString.CopyFrom(metadataBytes)
            }
        };

        var geometry = new CalculatorGraphConfig.Types.Node
        {
            Calculator =
                "mediapipe.tasks.vision.face_geometry." +
                "FaceGeometryPipelineCalculator",
            Options = CalculatorOptions.Parser.ParseFrom(
                WrapExtension(512499200u, geometryOptions.ToByteArray()))
        };
        geometry.InputStream.Add("IMAGE_SIZE:image_size");
        geometry.InputStream.Add("FACE_LANDMARKS:landmarks");
        geometry.InputSidePacket.Add("ENVIRONMENT:environment");
        geometry.OutputStream.Add("FACE_GEOMETRY:geometry");
        config.Node.Add(geometry);

        return config;
    }

    private static byte[] BuildEnvironmentGeneratorOptions()
    {
        var camera = new List<byte>();
        WriteFixed32Field(camera, 1, (float)VFoVDegrees);
        WriteFixed32Field(camera, 2, (float)Near);
        WriteFixed32Field(camera, 3, (float)Far);

        var environment = new List<byte>();
        WriteVarintField(environment, 1, 2);
        WriteLengthDelimitedField(environment, 2, camera.ToArray());

        var options = new List<byte>();
        WriteLengthDelimitedField(options, 1, environment.ToArray());
        return options.ToArray();
    }

    private static byte[] WrapExtension(uint fieldNumber, byte[] payload)
    {
        var bytes = new List<byte>();
        WriteVarint(bytes, ((ulong)fieldNumber << 3) | 2u);
        WriteVarint(bytes, (ulong)payload.Length);
        bytes.AddRange(payload);
        return bytes.ToArray();
    }

    private static List<Fixture> BuildFixtures(Metadata metadata)
    {
        int count = metadata.VertexBuffer.Count / 5;
        var cx = new float[count];
        var cy = new float[count];
        var cz = new float[count];

        for (int i = 0; i < count; ++i)
        {
            cx[i] = metadata.VertexBuffer[5 * i];
            cy[i] = metadata.VertexBuffer[5 * i + 1];
            cz[i] = metadata.VertexBuffer[5 * i + 2];
        }

        double midX = 0.5 * (cx.Min() + cx.Max());
        double midY = 0.5 * (cy.Min() + cy.Max());
        double meanZ = cz.Average(v => (double)v);
        double scale = 0.58 / Math.Max(cx.Max() - cx.Min(), cy.Max() - cy.Min());

        NormalizedLandmarkList baseLandmarks = BuildLandmarkList(count, i =>
            new[]
            {
                (float)(0.5 + scale * (cx[i] - midX)),
                (float)(0.5 - scale * (cy[i] - midY)),
                (float)(scale * (cz[i] - meanZ))
            });

        NormalizedLandmarkList translated = Transform(baseLandmarks, (x, y, z) =>
            new[] { x + 0.04f, y - 0.03f, z });
        NormalizedLandmarkList relativeZ = Transform(baseLandmarks, (x, y, z) =>
            new[] { x, y, 1.25f * z });

        return new List<Fixture>
        {
            new Fixture { Name = "base_16_9", Width = 480, Height = 270, Landmarks = baseLandmarks },
            new Fixture { Name = "translated_16_9", Width = 480, Height = 270, Landmarks = translated },
            new Fixture { Name = "relative_z_16_9", Width = 480, Height = 270, Landmarks = relativeZ },
            new Fixture { Name = "base_16_9_absolute_equivalent", Width = 1920, Height = 1080, Landmarks = baseLandmarks },
            new Fixture { Name = "base_4_3", Width = 640, Height = 480, Landmarks = baseLandmarks }
        };
    }

    private static NormalizedLandmarkList BuildLandmarkList(
        int count,
        Func<int, float[]> selector)
    {
        var result = new NormalizedLandmarkList();
        for (int i = 0; i < count; ++i)
        {
            float[] xyz = selector(i);
            result.Landmark.Add(new NormalizedLandmark
            {
                X = xyz[0],
                Y = xyz[1],
                Z = xyz[2]
            });
        }
        return result;
    }

    private static NormalizedLandmarkList Transform(
        NormalizedLandmarkList source,
        Func<float, float, float, float[]> transform)
    {
        var result = new NormalizedLandmarkList();
        foreach (NormalizedLandmark landmark in source.Landmark)
        {
            float[] xyz = transform(landmark.X, landmark.Y, landmark.Z);
            result.Landmark.Add(new NormalizedLandmark
            {
                X = xyz[0],
                Y = xyz[1],
                Z = xyz[2]
            });
        }
        return result;
    }

    private static Evaluation Evaluate(Fixture fixture, FaceGeometryProto geometry)
    {
        Require(geometry != null, "FaceGeometry is null.");
        Require(geometry.Mesh != null, "FaceGeometry mesh is null.");
        Require(geometry.PoseTransformMatrix != null, "Pose matrix is null.");

        float[,] pose = ReadMatrix(geometry.PoseTransformMatrix);
        int vertexSize = 5;
        int vertexCount = geometry.Mesh.VertexBuffer.Count / vertexSize;
        var meshPositions = new float[vertexCount * 3];
        bool finite = true;

        for (int i = 0; i < vertexCount; ++i)
        {
            for (int axis = 0; axis < 3; ++axis)
            {
                float value = geometry.Mesh.VertexBuffer[vertexSize * i + axis];
                meshPositions[3 * i + axis] = value;
                finite &= IsFinite(value);
            }
        }

        for (int row = 0; row < 4; ++row)
        {
            for (int col = 0; col < 4; ++col)
            {
                finite &= IsFinite(pose[row, col]);
            }
        }

        double lastRowError = Math.Max(
            Math.Max(Math.Abs(pose[3, 0]), Math.Abs(pose[3, 1])),
            Math.Max(Math.Abs(pose[3, 2]), Math.Abs(pose[3, 3] - 1.0)));

        var scales = new double[3];
        for (int col = 0; col < 3; ++col)
        {
            scales[col] = Math.Sqrt(
                pose[0, col] * pose[0, col] +
                pose[1, col] * pose[1, col] +
                pose[2, col] * pose[2, col]);
        }
        double meanScale = scales.Average();
        double scaleSpread = (scales.Max() - scales.Min()) / meanScale;

        var rotation = new double[3, 3];
        for (int row = 0; row < 3; ++row)
        {
            for (int col = 0; col < 3; ++col)
            {
                rotation[row, col] = pose[row, col] / meanScale;
            }
        }

        double orthogonalityError = 0.0;
        for (int row = 0; row < 3; ++row)
        {
            for (int col = 0; col < 3; ++col)
            {
                double dot = 0.0;
                for (int k = 0; k < 3; ++k)
                {
                    dot += rotation[k, row] * rotation[k, col];
                }
                orthogonalityError = Math.Max(
                    orthogonalityError,
                    Math.Abs(dot - (row == col ? 1.0 : 0.0)));
            }
        }

        double determinant =
            rotation[0, 0] * (rotation[1, 1] * rotation[2, 2] - rotation[1, 2] * rotation[2, 1]) -
            rotation[0, 1] * (rotation[1, 0] * rotation[2, 2] - rotation[1, 2] * rotation[2, 0]) +
            rotation[0, 2] * (rotation[1, 0] * rotation[2, 1] - rotation[1, 1] * rotation[2, 0]);

        double heightAtNear = 2.0 * Near * Math.Tan(0.5 * VFoVDegrees * Math.PI / 180.0);
        double widthAtNear = fixture.Width * heightAtNear / fixture.Height;
        double left = -0.5 * widthAtNear;
        double bottom = -0.5 * heightAtNear;
        double sumSq = 0.0;
        double maxError = 0.0;
        int negativeZCount = 0;

        Require(vertexCount == fixture.Landmarks.Landmark.Count, "Mesh/landmark count mismatch.");

        for (int i = 0; i < vertexCount; ++i)
        {
            double mx = meshPositions[3 * i];
            double my = meshPositions[3 * i + 1];
            double mz = meshPositions[3 * i + 2];
            double rx = pose[0, 0] * mx + pose[0, 1] * my + pose[0, 2] * mz + pose[0, 3];
            double ry = pose[1, 0] * mx + pose[1, 1] * my + pose[1, 2] * mz + pose[1, 3];
            double rz = pose[2, 0] * mx + pose[2, 1] * my + pose[2, 2] * mz + pose[2, 3];
            if (rz < 0.0)
            {
                ++negativeZCount;
            }
            double zPositive = -rz;
            double nearX = rx * Near / zPositive;
            double nearY = ry * Near / zPositive;
            double projectedX = (nearX - left) / widthAtNear;
            double projectedYTop = 1.0 - (nearY - bottom) / heightAtNear;
            double dx = projectedX - fixture.Landmarks.Landmark[i].X;
            double dy = projectedYTop - fixture.Landmarks.Landmark[i].Y;
            double error = Math.Sqrt(dx * dx + dy * dy);
            sumSq += error * error;
            maxError = Math.Max(maxError, error);
        }

        return new Evaluation
        {
            Name = fixture.Name,
            Width = fixture.Width,
            Height = fixture.Height,
            Serialized = geometry.ToByteArray(),
            Pose = pose,
            MeshPositions = meshPositions,
            MeshVertexCount = vertexCount,
            Finite = finite,
            LastRowMaxError = lastRowError,
            ScaleRelativeSpread = scaleSpread,
            OrthogonalityMaxError = orthogonalityError,
            RotationDeterminant = determinant,
            ReprojectionRms = Math.Sqrt(sumSq / vertexCount),
            ReprojectionMax = maxError,
            RuntimeNegativeZCount = negativeZCount
        };
    }

    private static float[,] ReadMatrix(MatrixData matrix)
    {
        Require(matrix.Rows == 4 && matrix.Cols == 4, "Pose matrix is not 4x4.");
        Require(matrix.PackedData.Count == 16, "Pose matrix has no 16-value payload.");
        var result = new float[4, 4];
        for (int row = 0; row < 4; ++row)
        {
            for (int col = 0; col < 4; ++col)
            {
                int index = matrix.Layout == MatrixData.Types.Layout.RowMajor
                    ? row * 4 + col
                    : col * 4 + row;
                result[row, col] = matrix.PackedData[index];
            }
        }
        return result;
    }

    private static double MaxAbsDifference(float[,] a, float[,] b)
    {
        double max = 0.0;
        for (int row = 0; row < a.GetLength(0); ++row)
        {
            for (int col = 0; col < a.GetLength(1); ++col)
            {
                max = Math.Max(max, Math.Abs(a[row, col] - b[row, col]));
            }
        }
        return max;
    }

    private static double MaxAbsDifference(float[] a, float[] b)
    {
        Require(a.Length == b.Length, "Array sizes differ.");
        double max = 0.0;
        for (int i = 0; i < a.Length; ++i)
        {
            max = Math.Max(max, Math.Abs(a[i] - b[i]));
        }
        return max;
    }

    private static byte[] ReadZipEntry(string zipPath, string entryName)
    {
        using (FileStream stream = File.OpenRead(zipPath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            ZipArchiveEntry entry = archive.GetEntry(entryName);
            Require(entry != null, "Metadata entry is absent from bundle.");
            using (Stream entryStream = entry.Open())
            using (var memory = new MemoryStream())
            {
                entryStream.CopyTo(memory);
                return memory.ToArray();
            }
        }
    }

    private static Metadata ParseMetadata(byte[] bytes)
    {
        var result = new Metadata();
        var reader = new ProtoReader(bytes);
        while (!reader.End)
        {
            uint tag = reader.ReadTag();
            int field = (int)(tag >> 3);
            int wire = (int)(tag & 7);
            if (field == 1 && wire == 2)
            {
                ParseMesh(reader.ReadBytes(), result);
            }
            else if (field == 2 && wire == 2)
            {
                result.Weights.Add(ParseWeight(reader.ReadBytes()));
            }
            else if (field == 3 && wire == 0)
            {
                result.InputSource = (int)reader.ReadVarint();
            }
            else
            {
                reader.Skip(wire);
            }
        }
        return result;
    }

    private static void ParseMesh(byte[] bytes, Metadata result)
    {
        var reader = new ProtoReader(bytes);
        while (!reader.End)
        {
            uint tag = reader.ReadTag();
            int field = (int)(tag >> 3);
            int wire = (int)(tag & 7);
            if (field == 1 && wire == 0)
            {
                result.VertexType = (int)reader.ReadVarint();
            }
            else if (field == 2 && wire == 0)
            {
                result.PrimitiveType = (int)reader.ReadVarint();
            }
            else if (field == 3 && wire == 2)
            {
                byte[] packed = reader.ReadBytes();
                Require(packed.Length % 4 == 0, "Packed vertex float payload is invalid.");
                for (int i = 0; i < packed.Length; i += 4)
                {
                    result.VertexBuffer.Add(ReadLittleEndianFloat(packed, i));
                }
            }
            else if (field == 3 && wire == 5)
            {
                result.VertexBuffer.Add(reader.ReadFloat());
            }
            else if (field == 4 && wire == 2)
            {
                var packedReader = new ProtoReader(reader.ReadBytes());
                while (!packedReader.End)
                {
                    result.IndexBuffer.Add((uint)packedReader.ReadVarint());
                }
            }
            else if (field == 4 && wire == 0)
            {
                result.IndexBuffer.Add((uint)reader.ReadVarint());
            }
            else
            {
                reader.Skip(wire);
            }
        }
    }

    private static WeightedRef ParseWeight(byte[] bytes)
    {
        var result = new WeightedRef();
        var reader = new ProtoReader(bytes);
        while (!reader.End)
        {
            uint tag = reader.ReadTag();
            int field = (int)(tag >> 3);
            int wire = (int)(tag & 7);
            if (field == 1 && wire == 0)
            {
                result.LandmarkId = (uint)reader.ReadVarint();
            }
            else if (field == 2 && wire == 5)
            {
                result.Weight = reader.ReadFloat();
            }
            else
            {
                reader.Skip(wire);
            }
        }
        return result;
    }

    private sealed class ProtoReader
    {
        private readonly byte[] data;
        private int position;

        public ProtoReader(byte[] data)
        {
            this.data = data;
        }

        public bool End => position >= data.Length;

        public uint ReadTag()
        {
            return (uint)ReadVarint();
        }

        public ulong ReadVarint()
        {
            ulong value = 0;
            for (int shift = 0; shift < 64; shift += 7)
            {
                Require(position < data.Length, "Unexpected end of varint.");
                byte next = data[position++];
                value |= (ulong)(next & 0x7f) << shift;
                if ((next & 0x80) == 0)
                {
                    return value;
                }
            }
            throw new InvalidDataException("Varint is too long.");
        }

        public byte[] ReadBytes()
        {
            int length = checked((int)ReadVarint());
            Require(length >= 0 && position + length <= data.Length, "Invalid length-delimited field.");
            var result = new byte[length];
            Buffer.BlockCopy(data, position, result, 0, length);
            position += length;
            return result;
        }

        public float ReadFloat()
        {
            Require(position + 4 <= data.Length, "Unexpected end of fixed32.");
            float result = ReadLittleEndianFloat(data, position);
            position += 4;
            return result;
        }

        public void Skip(int wire)
        {
            if (wire == 0)
            {
                ReadVarint();
            }
            else if (wire == 1)
            {
                Require(position + 8 <= data.Length, "Invalid fixed64.");
                position += 8;
            }
            else if (wire == 2)
            {
                int length = checked((int)ReadVarint());
                Require(length >= 0 && position + length <= data.Length, "Invalid skipped field.");
                position += length;
            }
            else if (wire == 5)
            {
                Require(position + 4 <= data.Length, "Invalid fixed32.");
                position += 4;
            }
            else
            {
                throw new InvalidDataException("Unsupported protobuf wire type " + wire);
            }
        }
    }

    private static float ReadLittleEndianFloat(byte[] bytes, int offset)
    {
        if (BitConverter.IsLittleEndian)
        {
            return BitConverter.ToSingle(bytes, offset);
        }
        var temp = new byte[4];
        for (int i = 0; i < 4; ++i)
        {
            temp[i] = bytes[offset + 3 - i];
        }
        return BitConverter.ToSingle(temp, 0);
    }

    private static string ComputeBasisIdentity(IEnumerable<WeightedRef> weights)
    {
        var canonical = new StringBuilder();
        foreach (WeightedRef item in weights)
        {
            canonical.Append(item.LandmarkId.ToString(CultureInfo.InvariantCulture));
            canonical.Append(':');
            canonical.Append(FloatBits(item.Weight).ToString("X8", CultureInfo.InvariantCulture));
            canonical.Append('\n');
        }
        return Sha256Hex(Encoding.ASCII.GetBytes(canonical.ToString()));
    }

    private static uint FloatBits(float value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        return BitConverter.ToUInt32(bytes, 0);
    }

    private static void WriteVarintField(List<byte> bytes, int field, ulong value)
    {
        WriteVarint(bytes, ((ulong)field << 3) | 0u);
        WriteVarint(bytes, value);
    }

    private static void WriteFixed32Field(List<byte> bytes, int field, float value)
    {
        WriteVarint(bytes, ((ulong)field << 3) | 5u);
        byte[] payload = BitConverter.GetBytes(value);
        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(payload);
        }
        bytes.AddRange(payload);
    }

    private static void WriteLengthDelimitedField(List<byte> bytes, int field, byte[] payload)
    {
        WriteVarint(bytes, ((ulong)field << 3) | 2u);
        WriteVarint(bytes, (ulong)payload.Length);
        bytes.AddRange(payload);
    }

    private static void WriteVarint(List<byte> bytes, ulong value)
    {
        while (value >= 0x80)
        {
            bytes.Add((byte)((value & 0x7f) | 0x80));
            value >>= 7;
        }
        bytes.Add((byte)value);
    }

    private static string Sha256Hex(byte[] bytes)
    {
        using (SHA256 sha = SHA256.Create())
        {
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty);
        }
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static string F(double value)
    {
        return value.ToString("G17", CultureInfo.InvariantCulture);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
}
