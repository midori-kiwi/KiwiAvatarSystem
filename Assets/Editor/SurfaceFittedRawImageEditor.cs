using UnityEditor;
using UnityEditor.UI;
using UnityEngine;


[CustomEditor(typeof(SurfaceFittedRawImage))]
[CanEditMultipleObjects]
public class SurfaceFittedRawImageEditor
    : RawImageEditor
{
    private SerializedProperty targetRenderer;
    private SerializedProperty autoFindRenderer;

    private SerializedProperty horizontalSegments;
    private SerializedProperty verticalSegments;

    private SerializedProperty surfaceOffset;

    private SerializedProperty fitOnStart;


    protected override void OnEnable()
    {
        base.OnEnable();


        targetRenderer =
            serializedObject.FindProperty(
                "targetRenderer"
            );


        autoFindRenderer =
            serializedObject.FindProperty(
                "autoFindRenderer"
            );


        horizontalSegments =
            serializedObject.FindProperty(
                "horizontalSegments"
            );


        verticalSegments =
            serializedObject.FindProperty(
                "verticalSegments"
            );


        surfaceOffset =
            serializedObject.FindProperty(
                "surfaceOffset"
            );


        fitOnStart =
            serializedObject.FindProperty(
                "fitOnStart"
            );
    }


    public override void OnInspectorGUI()
    {
        // RawImage標準
        base.OnInspectorGUI();


        serializedObject.Update();


        EditorGUILayout.Space(10);


        EditorGUILayout.LabelField(
            "Kiwi Surface",
            EditorStyles.boldLabel
        );


        EditorGUILayout.PropertyField(
            targetRenderer
        );


        EditorGUILayout.PropertyField(
            autoFindRenderer
        );


        EditorGUILayout.Space(6);


        EditorGUILayout.LabelField(
            "Surface Mesh",
            EditorStyles.boldLabel
        );


        EditorGUILayout.PropertyField(
            horizontalSegments
        );


        EditorGUILayout.PropertyField(
            verticalSegments
        );


        EditorGUILayout.Space(6);


        EditorGUILayout.LabelField(
            "Surface Offset",
            EditorStyles.boldLabel
        );


        EditorGUILayout.PropertyField(
            surfaceOffset
        );


        EditorGUILayout.Space(6);


        EditorGUILayout.LabelField(
            "Automatic Fitting",
            EditorStyles.boldLabel
        );


        EditorGUILayout.PropertyField(
            fitOnStart
        );


        serializedObject.ApplyModifiedProperties();


        // =============================================
        // Play中に手動再フィット
        // =============================================

        EditorGUILayout.Space(10);


        if (
            GUILayout.Button(
                "Refit Surface"
            )
        )
        {
            foreach (
                Object obj
                in targets
            )
            {
                SurfaceFittedRawImage image =
                    obj as
                    SurfaceFittedRawImage;


                if (image != null)
                {
                    image.RefitSurface();
                }
            }
        }
    }
}