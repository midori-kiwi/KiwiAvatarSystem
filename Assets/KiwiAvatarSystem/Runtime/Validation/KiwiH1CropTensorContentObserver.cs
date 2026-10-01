#if DEVELOPMENT_BUILD || UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.Rendering;

internal sealed class KiwiH1CropTensorContentObserver : MonoBehaviour
{
    const string Enable="KIWI_H1_CROP_TENSOR_CONTENT_OBSERVER", Dir="KIWI_H1_CROP_TENSOR_EVIDENCE_DIR", Max="KIWI_H1_CROP_TENSOR_MAX";
    const string ShaderPath="KiwiValidation/KiwiTensorSnapshotCopyV44_55_22";
    const int Size=192, Plane=Size*Size, Count=Plane*3, CropBytes=Size*Size*4, Threads=256, MaxInFlight=4;
    const float Tol=1e-7f;
    static readonly CultureInfo Inv=CultureInfo.InvariantCulture;
    static KiwiH1CropTensorContentObserver s_instance;
    readonly object _gate=new object();
    readonly List<Capture> _active=new List<Capture>(8);
    ComputeShader _shader; int _kernel=-1,_max=256,_attempted,_submitted,_completed,_faults,_skipIdentity,_skipMode,_skipBusy; StreamWriter _writer; bool _ready; string _path;

    sealed class Capture
    {
        internal int index,sourceGeneration,trackerGeneration,cameraGeneration,trackingSessionGeneration,sourceWidth,sourceHeight,backingCount;
        internal ulong sourceFrameId; internal long sourceHostTicks; internal string cropFormat,cropMatrixBits,failure=""; internal bool cropSrgb,cropDone,tensorDone,failed;
        internal ComputeBuffer snapshot; internal byte[] crop; internal float[] tensor;
    }
    readonly struct Match
    {
        internal readonly string origin,order; internal readonly int bit,numeric,first,expectedBits,actualBits; internal readonly float max; internal readonly double mean;
        internal Match(string o,string r,int b,int n,float x,double m,int f,int e,int a){origin=o;order=r;bit=b;numeric=n;max=x;mean=m;first=f;expectedBits=e;actualBits=a;}
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Install()
    {
        if(!ReadBool(Enable)||s_instance!=null)return;
        var go=new GameObject("[Kiwi] H1 Crop Tensor Content Observer"){hideFlags=HideFlags.DontSave}; DontDestroyOnLoad(go); s_instance=go.AddComponent<KiwiH1CropTensorContentObserver>();
    }
    void Awake(){if(s_instance!=null&&s_instance!=this){Destroy(gameObject);return;}s_instance=this;try{Init();}catch(Exception e){Fail("INIT_"+e.GetType().Name+" "+e.Message);}}
    void Init()
    {
        if(!SystemInfo.supportsComputeShaders||!SystemInfo.supportsAsyncGPUReadback){Fail("GPU_CAPABILITY_MISSING");return;}
        if(!BitConverter.IsLittleEndian){Fail("NON_LITTLE_ENDIAN_UNSUPPORTED");return;}
        string d=Environment.GetEnvironmentVariable(Dir); if(string.IsNullOrWhiteSpace(d)||!Directory.Exists(d)){Fail("EVIDENCE_DIR_INVALID");return;}
        _max=ReadInt(Max,256,1,1024); _shader=Resources.Load<ComputeShader>(ShaderPath); if(_shader==null){Fail("SNAPSHOT_SHADER_MISSING");return;} _kernel=_shader.FindKernel("CopyTensorSnapshot");
        _path=Path.Combine(d,"h1_crop_tensor_content_"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff",Inv)+".csv");
        _writer=new StreamWriter(new FileStream(_path,FileMode.CreateNew,FileAccess.Write,FileShare.Read),new UTF8Encoding(false));
        _writer.WriteLine("captureIndex,sourceFrameId,sourceFrameIdDomain,sourceGeneration,sourceHostTicks,trackerGeneration,cameraGeneration,trackingSessionGeneration,sourceWidth,sourceHeight,cropTextureFormat,cropTextureSrgb,cropReadbackFormat,tensorShape,tensorDtype,declaredTensorLayout,declaredCoordOrigin,tensorBackingElementCapacity,cropMatrixBitsHex,cropHashEncoding,tensorHashEncoding,cropSha256,tensorSha256,cropByteCount,tensorElementCount,classification,origin,channelOrder,bitMismatchCount,numericMismatchCount,maxAbs,meanAbs,firstMismatchTensorIndex,firstExpectedBitsHex,firstActualBitsHex,observerFault"); _writer.Flush();
        _ready=true; Debug.Log("[Kiwi H1 CropTensor] ARMED path="+_path+" max="+_max+" directGraphicsOnly=1 maxInFlight="+MaxInFlight+" extraBlit=0 extraWorker=0 blockingWait=0 performanceAuthority=0");
    }
    void Fail(string s){_faults++;_ready=false;Debug.LogError("[Kiwi H1 CropTensor] "+s);}

    internal static void TryCapture(ulong id,bool native,int sourceGen,long ticks,int trackerGen,int cameraGen,int sessionGen,int width,int height,Matrix4x4 matrix,RenderTexture crop,Tensor<float> input,bool direct)
    {
        var x=s_instance;if(x==null||!x._ready)return;x.CaptureNow(id,native,sourceGen,ticks,trackerGen,cameraGen,sessionGen,width,height,matrix,crop,input,direct);
    }
    void CaptureNow(ulong id,bool native,int sourceGen,long ticks,int trackerGen,int cameraGen,int sessionGen,int width,int height,Matrix4x4 matrix,RenderTexture crop,Tensor<float> input,bool direct)
    {
        if(_attempted>=_max)return;if(!direct){_skipMode++;return;}if(!native||id==0||ticks<=0||sourceGen<=0||cameraGen<=0||sessionGen<=0){_skipIdentity++;return;}lock(_gate){if(_active.Count>=MaxInFlight){_skipBusy++;return;}}
        var c=new Capture{index=++_attempted,sourceFrameId=id,sourceGeneration=sourceGen,sourceHostTicks=ticks,trackerGeneration=trackerGen,cameraGeneration=cameraGen,trackingSessionGeneration=sessionGen,sourceWidth=width,sourceHeight=height,cropFormat=crop!=null?crop.format.ToString():"",cropSrgb=crop!=null&&crop.sRGB,cropMatrixBits=MatrixBits(matrix)};
        if(crop==null||!crop.IsCreated()||crop.width!=Size||crop.height!=Size||input==null||input.shape.length!=Count){FaultRow(c,"INPUT_CONTRACT_INVALID");return;}
        var data=input.dataOnBackend as ComputeTensorData; var buffer=data!=null?data.buffer:null; if(buffer==null||!buffer.IsValid()||buffer.count<Count||buffer.stride!=sizeof(float)){FaultRow(c,"INPUT_BACKING_INVALID");return;} c.backingCount=buffer.count;
        try
        {
            c.snapshot=new ComputeBuffer(Count,sizeof(float),ComputeBufferType.Structured){name="Kiwi H1 Input Snapshot "+c.index}; lock(_gate)_active.Add(c);
            var cb=new CommandBuffer{name="Kiwi H1 CropTensor "+c.index}; try
            {
                cb.SetComputeIntParam(_shader,"_Count",Count); cb.SetComputeBufferParam(_shader,_kernel,"_Source",buffer); cb.SetComputeBufferParam(_shader,_kernel,"_Destination",c.snapshot);
                cb.DispatchCompute(_shader,_kernel,(Count+Threads-1)/Threads,1,1); cb.RequestAsyncReadback(crop,0,TextureFormat.RGBA32,r=>OnCrop(c,r)); cb.RequestAsyncReadback(c.snapshot,r=>OnTensor(c,r)); Graphics.ExecuteCommandBuffer(cb); _submitted++;
            } finally{cb.Release();}
        }
        catch(Exception e){lock(_gate){c.failed=true;c.failure="SUBMIT_"+e.GetType().Name;c.cropDone=c.tensorDone=true;}_faults++;}
    }
    void FaultRow(Capture c,string s){_faults++;c.failed=true;c.failure=s;Write(c,default,"OBSERVER_FAULT");}
    static void OnCrop(Capture c,AsyncGPUReadbackRequest r)
    {
        var x=s_instance;if(x==null||c==null)return;try{if(r.hasError)throw new InvalidOperationException();var d=r.GetData<byte>();if(d.Length!=CropBytes)throw new InvalidOperationException("COUNT");var a=new byte[CropBytes];d.CopyTo(a);lock(x._gate){c.crop=a;c.cropDone=true;}}catch(Exception e){lock(x._gate){c.failed=true;c.failure="CROP_"+e.GetType().Name;c.cropDone=true;}x._faults++;}
    }
    static void OnTensor(Capture c,AsyncGPUReadbackRequest r)
    {
        var x=s_instance;if(x==null||c==null)return;try{if(r.hasError)throw new InvalidOperationException();var d=r.GetData<float>();if(d.Length!=Count)throw new InvalidOperationException("COUNT");var a=new float[Count];d.CopyTo(a);lock(x._gate){c.tensor=a;c.tensorDone=true;}}catch(Exception e){lock(x._gate){c.failed=true;c.failure="TENSOR_"+e.GetType().Name;c.tensorDone=true;}x._faults++;}
    }
    void Update()
    {
        List<Capture> done=null; lock(_gate){for(int i=_active.Count-1;i>=0;i--){var c=_active[i];if(!c.cropDone||!c.tensorDone)continue;if(done==null)done=new List<Capture>();done.Add(c);_active.RemoveAt(i);}}
        if(done==null)return;for(int i=done.Count-1;i>=0;i--)FinalizeCapture(done[i]);
    }
    void FinalizeCapture(Capture c)
    {
        try{if(c.failed||c.crop==null||c.tensor==null){Write(c,default,"OBSERVER_FAULT");return;}var m=Best(c.crop,c.tensor);bool declared=m.origin=="TOP_LEFT"&&m.order=="RGB";string cls=declared&&m.bit==0?"DECLARED_EXACT_BITS":declared&&m.numeric==0&&m.max<=Tol?"DECLARED_NUMERIC_EQ_1E7":m.bit==0?"ALTERNATE_MAPPING_EXACT_BITS":m.numeric==0&&m.max<=Tol?"ALTERNATE_MAPPING_NUMERIC_EQ_1E7":"MISMATCH";Write(c,m,cls);_completed++;}
        catch(Exception e){_faults++;c.failed=true;c.failure="FINALIZE_"+e.GetType().Name;Write(c,default,"OBSERVER_FAULT");}finally{Release(c);}
    }

    static Match Best(byte[] crop,float[] tensor)
    {
        int[][] p={new[]{0,1,2},new[]{0,2,1},new[]{1,0,2},new[]{1,2,0},new[]{2,0,1},new[]{2,1,0}};string[] n={"RGB","RBG","GRB","GBR","BRG","BGR"};bool set=false;Match best=default;
        for(int o=0;o<2;o++)for(int k=0;k<6;k++){var m=Eval(crop,tensor,o==0,p[k],n[k]);if(!set||Better(m,best)){best=m;set=true;}}return best;
    }
    static Match Eval(byte[] crop,float[] tensor,bool topLeft,int[] map,string order)
    {
        int bit=0,num=0,first=-1,eb0=0,ab0=0;float max=0;double sum=0;
        // Inference Engine 2.4.1 defines CoordOrigin.TopLeft by sampling
        // texture row (Height - 1 - tensorY) into tensor row tensorY.
        // The raw AsyncGPUReadback row index is therefore not itself the
        // tensor coordinate origin label.
        for(int ch=0;ch<3;ch++)for(int y=0;y<Size;y++){int cy=topLeft?Size-1-y:y,row=cy*Size*4,tbase=ch*Plane+y*Size;for(int x=0;x<Size;x++){int ti=tbase+x;float e=crop[row+x*4+map[ch]]/255f,a=tensor[ti];int eb=BitConverter.SingleToInt32Bits(e),ab=BitConverter.SingleToInt32Bits(a);if(eb!=ab){bit++;if(first<0){first=ti;eb0=eb;ab0=ab;}}float d;if(float.IsNaN(a)||float.IsInfinity(a)){num++;d=float.PositiveInfinity;}else{d=Mathf.Abs(a-e);if(d>Tol)num++;}if(d>max)max=d;sum+=d;}}
        return new Match(topLeft?"TOP_LEFT":"BOTTOM_LEFT",order,bit,num,max,sum/Count,first,eb0,ab0);
    }
    static bool Better(Match a,Match b){if(a.bit!=b.bit)return a.bit<b.bit;if(a.numeric!=b.numeric)return a.numeric<b.numeric;if(a.max!=b.max)return a.max<b.max;return a.mean<b.mean;}

    void Write(Capture c,Match m,string cls)
    {
        if(_writer==null||c==null)return;string ch=c.crop==null?"":Hash(c.crop),th=c.tensor==null?"":Hash(c.tensor);
        _writer.WriteLine(c.index+","+c.sourceFrameId+",NATIVE_PRESENTED_SEQUENCE,"+c.sourceGeneration+","+c.sourceHostTicks+","+c.trackerGeneration+","+c.cameraGeneration+","+c.trackingSessionGeneration+","+c.sourceWidth+","+c.sourceHeight+","+Csv(c.cropFormat)+","+B(c.cropSrgb)+",RGBA32,1x3x192x192,FLOAT32,NCHW,TOP_LEFT,"+c.backingCount+","+Csv(c.cropMatrixBits)+",RGBA8_READBACK_ROW_BYTES,IEEE754_FLOAT32_LITTLE_ENDIAN,"+ch+","+th+","+(c.crop==null?0:c.crop.Length)+","+(c.tensor==null?0:c.tensor.Length)+","+Csv(cls)+","+Csv(m.origin)+","+Csv(m.order)+","+m.bit+","+m.numeric+","+m.max.ToString("R",Inv)+","+m.mean.ToString("R",Inv)+","+m.first+","+Hex(m.expectedBits)+","+Hex(m.actualBits)+","+Csv(c.failure));_writer.Flush();
    }
    static string MatrixBits(Matrix4x4 m){var b=new StringBuilder(143);for(int i=0;i<16;i++){if(i>0)b.Append('|');b.Append(unchecked((uint)BitConverter.SingleToInt32Bits(m[i])).ToString("X8",Inv));}return b.ToString();}
    static string Hash(byte[] a){using(var s=SHA256.Create())return BytesHex(s.ComputeHash(a));}
    static string Hash(float[] a){var b=new byte[a.Length*sizeof(float)];Buffer.BlockCopy(a,0,b,0,b.Length);return Hash(b);}
    static string BytesHex(byte[] a){var b=new StringBuilder(a.Length*2);for(int i=0;i<a.Length;i++)b.Append(a[i].ToString("X2",Inv));return b.ToString();}
    static string Hex(int v)=>unchecked((uint)v).ToString("X8",Inv); static string B(bool v)=>v?"1":"0"; static string Csv(string v)=>string.IsNullOrEmpty(v)?"":"\""+v.Replace("\"","\"\"")+"\"";
    static bool ReadBool(string n){string v=Environment.GetEnvironmentVariable(n);return v=="1"||string.Equals(v,"true",StringComparison.OrdinalIgnoreCase)||string.Equals(v,"yes",StringComparison.OrdinalIgnoreCase)||string.Equals(v,"on",StringComparison.OrdinalIgnoreCase);}
    static int ReadInt(string n,int f,int min,int max)=>int.TryParse(Environment.GetEnvironmentVariable(n),NumberStyles.Integer,Inv,out int v)?Mathf.Clamp(v,min,max):f;
    static void Release(Capture c){if(c?.snapshot==null)return;try{c.snapshot.Release();}catch{}c.snapshot=null;}
    void OnDisable(){_ready=false;lock(_gate){for(int i=0;i<_active.Count;i++)Release(_active[i]);_active.Clear();}if(_writer!=null){_writer.Flush();_writer.Dispose();_writer=null;}Debug.Log("[Kiwi H1 CropTensor] COMPLETE attempted="+_attempted+" submitted="+_submitted+" completed="+_completed+" faults="+_faults+" skipIdentity="+_skipIdentity+" skipMode="+_skipMode+" skipBusy="+_skipBusy+" path="+(_path??""));if(s_instance==this)s_instance=null;}
}
#endif
