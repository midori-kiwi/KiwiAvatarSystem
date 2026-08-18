using UnityEditor;
using UnityEditor.UI;


[CustomEditor(typeof(CurvedRawImage))]
[CanEditMultipleObjects]
public class CurvedRawImageEditor : RawImageEditor
{
    private SerializedProperty horizontalSegments;
    private SerializedProperty verticalSegments;

    private SerializedProperty horizontalCurveDepth;
    private SerializedProperty verticalCurveDepth;

    private SerializedProperty centerBulge;


    protected override void OnEnable()
    {
        base.OnEnable();

        horizontalSegments =
            serializedObject.FindProperty(
                "horizontalSegments"
            );

        verticalSegments =
            serializedObject.FindProperty(
                "verticalSegments"
            );

        horizontalCurveDepth =
            serializedObject.FindProperty(
                "horizontalCurveDepth"
            );

        verticalCurveDepth =
            serializedObject.FindProperty(
                "verticalCurveDepth"
            );

        centerBulge =
            serializedObject.FindProperty(
                "centerBulge"
            );
    }


    public override void OnInspectorGUI()
    {
        // =========================================
        // UnityïWèÄRawImageê›íË
        // Texture / Material / UV RectÇ»Ç«
        // =========================================

        base.OnInspectorGUI();


        // =========================================
        // CurvedRawImageê›íË
        // =========================================

        serializedObject.Update();

        EditorGUILayout.Space(10);

        EditorGUILayout.LabelField(
            "Curved Mesh",
            EditorStyles.boldLabel
        );


        EditorGUILayout.PropertyField(
            horizontalSegments
        );

        EditorGUILayout.PropertyField(
            verticalSegments
        );


        EditorGUILayout.Space(5);

        EditorGUILayout.LabelField(
            "Curvature",
            EditorStyles.boldLabel
        );


        EditorGUILayout.PropertyField(
            horizontalCurveDepth
        );

        EditorGUILayout.PropertyField(
            verticalCurveDepth
        );


        EditorGUILayout.Space(5);

        EditorGUILayout.LabelField(
            "Shape",
            EditorStyles.boldLabel
        );


        EditorGUILayout.PropertyField(
            centerBulge
        );


        serializedObject.ApplyModifiedProperties();
    }
}