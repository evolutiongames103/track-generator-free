using UnityEngine;
using UnityEditor;

// TrackGeneratorEditor.cs

namespace EvolutionGames.RacingTrack.Editor
{
    [CustomEditor(typeof(TrackGenerator))]
    public class TrackGeneratorEditor : UnityEditor.Editor
    {
        bool             _showLayout = true;
        SerializedObject _layoutSO;

        // cached layout reference for pre-scan button
        TrackLayoutGenerator _layout;

        void OnEnable()
        {
            var gen = (TrackGenerator)target;
            _layout = gen.GetComponent<TrackLayoutGenerator>();
            if (_layout != null) _layoutSO = new SerializedObject(_layout);
        }

        public override void OnInspectorGUI()
        {
            var generator = (TrackGenerator)target;
            _layout = generator.GetComponent<TrackLayoutGenerator>();

            // ── Track Definition ──────────────────────────────────────────────
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("definition"),
                                          new GUIContent("Track Definition"));

            // ── NEW: Demo Car field ───────────────────────────────────────────
            EditorGUILayout.PropertyField(serializedObject.FindProperty("demoCar"),
                                          new GUIContent("Demo Car"));
            // ──────────────────────────────────────────────────────────────────

            serializedObject.ApplyModifiedProperties();

            if (generator.definition == null)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox("Assign a TrackDefinition to generate.",
                                        MessageType.Warning);
            }

            EditorGUILayout.Space(8);

            // ── Layout settings ───────────────────────────────────────────────
            if (_layout == null)
            {
                EditorGUILayout.HelpBox("TrackLayoutGenerator component missing.",
                                        MessageType.Error);
            }
            else
            {
                if (_layoutSO == null || _layoutSO.targetObject == null)
                    _layoutSO = new SerializedObject(_layout);

                _showLayout = EditorGUILayout.Foldout(_showLayout, "Layout Settings",
                                                       true, EditorStyles.foldoutHeader);
                if (_showLayout)
                {
                    EditorGUI.indentLevel++;
                    _layoutSO.Update();

                    EditorGUILayout.LabelField("Generation", EditorStyles.boldLabel);
                    EditorGUILayout.PropertyField(_layoutSO.FindProperty("seed"),
                                                  new GUIContent("Seed"));
                    EditorGUILayout.PropertyField(_layoutSO.FindProperty("layoutVariance"),
                                                  new GUIContent("Layout Variance"));
                    EditorGUILayout.PropertyField(_layoutSO.FindProperty("openEnded"),
                                                  new GUIContent("Open Ended"));

                    EditorGUILayout.Space(4);

                    EditorGUILayout.LabelField("Dimensions", EditorStyles.boldLabel);
                    EditorGUILayout.PropertyField(_layoutSO.FindProperty("trackLength"),
                                                  new GUIContent("Track Length (m)"));

                    EditorGUILayout.Space(4);

                    // Removed Speed and Elevation sections entirely

                    EditorGUILayout.Space(6);

                    // ── Seed pre-screening ────────────────────────────────────
                    EditorGUILayout.LabelField("Seed Pre-screening", EditorStyles.boldLabel);

                    var autoScanProp = _layoutSO.FindProperty("autoPreScan");
                    EditorGUILayout.PropertyField(autoScanProp,
                                                  new GUIContent("Auto Pre-scan"));

                    // pool state display
                    EditorGUI.BeginDisabledGroup(true);
                    EditorGUILayout.IntField(new GUIContent("Good Seeds"),
                                             _layout.goodSeedCount);
                    EditorGUILayout.IntField(new GUIContent("Bad Seeds"),
                                             _layout.badSeedCount);
                    EditorGUI.EndDisabledGroup();

                    // manual pre-scan button — only shown when auto is off
                    if (!autoScanProp.boolValue)
                    {
                        EditorGUILayout.Space(4);
                        GUI.backgroundColor = new Color(0.6f, 0.8f, 1f);
                        if (GUILayout.Button("Pre-scan Seeds", GUILayout.Height(28)))
                        {
                            if (generator.definition != null)
                            {
                                _layout.PreScanSeeds(generator.definition);
                                EditorUtility.SetDirty(_layout);
                            }
                            else
                            {
                                Debug.LogWarning("[TrackGeneratorEditor] " +
                                                 "Assign a TrackDefinition before pre-scanning.");
                            }
                        }
                        GUI.backgroundColor = Color.white;
                    }

                    _layoutSO.ApplyModifiedProperties();
                    EditorGUI.indentLevel--;
                }
            }

            EditorGUILayout.Space(12);

            // ── Buttons ───────────────────────────────────────────────────────
            GUI.backgroundColor = new Color(0.3f, 0.6f, 1f);
            if (GUILayout.Button("Generate Track", GUILayout.Height(40)))
            {
                Undo.RecordObject(generator.gameObject, "Generate Track");
                if (_layout != null) Undo.RecordObject(_layout, "Generate Track");
                generator.Generate();
                EditorUtility.SetDirty(generator.gameObject);
                if (_layout != null) EditorUtility.SetDirty(_layout);
            }
            GUI.backgroundColor = Color.white;

            EditorGUILayout.Space(4);

            GUI.backgroundColor = new Color(0.9f, 0.4f, 0.4f);
            if (GUILayout.Button("Clear Track", GUILayout.Height(28)))
            {
                Undo.RecordObject(generator.gameObject, "Clear Track");
                generator.ClearTrack();
                EditorUtility.SetDirty(generator.gameObject);
            }
            GUI.backgroundColor = Color.white;
        }
    }
}