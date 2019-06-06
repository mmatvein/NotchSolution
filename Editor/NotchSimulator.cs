﻿//#define NOTCH_SOLUTION_DEBUG_TRANSITIONS

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Experimental.SceneManagement;
using UnityEditor.SceneManagement;
using UnityEditor.ShortcutManagement;
using UnityEngine;
using UnityEngine.EventSystems;

namespace E7.NotchSolution
{
    public class NotchSimulator : EditorWindow
    {
        internal static NotchSimulator win;

        [MenuItem("Window/General/Notch Simulator")]
        public static void ShowWindow()
        {
            win = (NotchSimulator)EditorWindow.GetWindow(typeof(NotchSimulator));
            win.titleContent = new GUIContent("Notch Simulator");
        }
        /// <summary>
        /// It is currently active only when Notch Simulator tab is present.
        /// </summary>
        void OnGUI()
        {
            win = this;
            //Sometimes even with flag I can see it in hierarchy until I move a mouse over it??
            EditorApplication.RepaintHierarchyWindow();

            bool enableSimulation = NotchSimulatorUtility.enableSimulation;
            EditorGUI.BeginChangeCheck();

#if UNITY_2019_1_OR_NEWER
            string shortcut = ShortcutManager.instance.GetShortcutBinding(NotchSolutionShortcuts.toggleSimulationShortcut).ToString();
            if (string.IsNullOrEmpty(shortcut)) shortcut = "None";
            string label = $"Simulate({shortcut})";
#else
            string label = "Simulate";
#endif
            
            NotchSimulatorUtility.enableSimulation = EditorGUILayout.BeginToggleGroup(label, NotchSimulatorUtility.enableSimulation);
            EditorGUI.indentLevel++;

            NotchSimulatorUtility.selectedDevice = (SimulationDevice)EditorGUILayout.EnumPopup(NotchSimulatorUtility.selectedDevice);
            NotchSimulatorUtility.flipOrientation = EditorGUILayout.Toggle("Flip Orientation", NotchSimulatorUtility.flipOrientation);

            var simulationDevice = SimulationDatabase.db[NotchSimulatorUtility.selectedDevice];

            //Draw warning about wrong aspect ratio
            if (enableSimulation)
            {
                ScreenOrientation gameViewOrientation = NotchSimulatorUtility.GetGameViewOrientation();

                Vector2 simSize = gameViewOrientation == ScreenOrientation.Portrait ?
                 simulationDevice.screenSize : new Vector2(simulationDevice.screenSize.y, simulationDevice.screenSize.x);

                Vector2 gameViewSize = NotchSimulatorUtility.GetMainGameViewSize();
                if (gameViewOrientation == ScreenOrientation.Landscape)
                {
                    var flip = gameViewSize.x;
                    gameViewSize.x = gameViewSize.y;
                    gameViewSize.y = flip;
                }

                var simAspect = NotchSolutionUtility.ScreenRatio(simulationDevice.screenSize);
                var gameViewAspect = NotchSolutionUtility.ScreenRatio(gameViewSize);
                var aspectDiff = Math.Abs((simAspect.x / simAspect.y) - (gameViewAspect.x / gameViewAspect.y));
                if (aspectDiff > 0.01f)
                {
                    EditorGUILayout.HelpBox($"The selected simulation device has an aspect ratio of {simAspect.y}:{simAspect.x} ({simulationDevice.screenSize.y}x{simulationDevice.screenSize.x}) but your game view is currently in aspect {gameViewAspect.y}:{gameViewAspect.x} ({gameViewSize.y}x{gameViewSize.x}). The overlay mockup will be stretched from its intended ratio.", MessageType.Warning);
                }
            }

            EditorGUI.indentLevel--;
            EditorGUILayout.EndToggleGroup();
            bool changed = EditorGUI.EndChangeCheck();

            if (changed)
            {
                UpdateAllMockups();
            }

            UpdateSimulatorTargets();
        }

        /// <summary>
        /// Get all <see cref="INotchSimulatorTarget"> and update them.
        /// </summary>
        internal static void UpdateSimulatorTargets()
        {
            var simulatedRect = NotchSimulatorUtility.enableSimulation ? NotchSimulatorUtility.SimulatorSafeAreaRelative : new Rect(0, 0, 1, 1);

            //This value could be used by the component statically.
            NotchSolutionUtility.SimulateSafeAreaRelative = simulatedRect;

            var normalSceneSimTargets = GameObject.FindObjectsOfType<UIBehaviour>().OfType<INotchSimulatorTarget>();
            foreach (var nst in normalSceneSimTargets)
            {
                nst.SimulatorUpdate(simulatedRect);
            }

            //Now find one in the prefab mode scene as well
            PrefabStage prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage != null)
            {
                var prefabSceneSimTargets = prefabStage.stageHandle.FindComponentsOfType<UIBehaviour>().OfType<INotchSimulatorTarget>();
                foreach (var nst in prefabSceneSimTargets)
                {
                    nst.SimulatorUpdate(simulatedRect);
                }
            }
        }

        private const string prefix = "NoSo";
        private const string mockupCanvasName = prefix + "-MockupCanvas";
        private const HideFlags overlayCanvasFlag = HideFlags.HideAndDontSave;

        private static MockupCanvas mockupCanvas;
        private static MockupCanvas prefabMockupCanvas;

        /// <summary>
        /// This need to return both from normal scene and prefab environment scene.
        /// </summary>
        private static IEnumerable<MockupCanvas> AllMockupCanvases
        {
            get
            {
                if (mockupCanvas != null)
                {
                    yield return mockupCanvas;
                }
                if (prefabMockupCanvas != null)
                {
                    yield return prefabMockupCanvas;
                }
            }
        }

        /// <summary>
        /// We lose all events on entering play mode, use this to register the event and also make a canvas again
        /// after it was destroyed by the event (that now disappeared)
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AddOverlayInPlayMode()
        {
            UpdateAllMockups();
        }

        /// <summary>
        /// This is called even if Notch Simulator tab is not present on the screen.
        /// Also have to handle if we reload scripts while in prefab mode.
        /// </summary>
        [UnityEditor.Callbacks.DidReloadScripts]
        private static void OnScriptsReloaded()
        {
            //DebugTransitions($"Script reloaded PLAY {EditorApplication.isPlaying} PLAY or WILL CHANGE {EditorApplication.isPlayingOrWillChangePlaymode}");

            //Avoid script reload due to entering playmode
            if (EditorApplication.isPlayingOrWillChangePlaymode == false)
            {
                UpdateAllMockups();
            }
        }

        private static void DestroyHiddenCanvas()
        {
            if (mockupCanvas != null)
            {
                GameObject.DestroyImmediate(mockupCanvas.gameObject);
            }
        }

        private static bool eventAdded = false;

        internal static void UpdateAllMockups()
        {
            EnsureCanvasAndEventSetup();

            //Make the editing environment contains an another copy of mockup canvas.
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if(prefabStage != null)
            {
                EnsureCanvasAndEventSetup(prefabStage: prefabStage);
            }

            bool enableSimulation = NotchSimulatorUtility.enableSimulation;
            if (enableSimulation)
            {
                //Landscape has an alias that turns ToString into LandscapeLeft lol
                var orientationString = NotchSimulatorUtility.GetGameViewOrientation() == ScreenOrientation.Landscape ? nameof(ScreenOrientation.Landscape) : nameof(ScreenOrientation.Portrait);
                SimulationDevice simDevice = NotchSimulatorUtility.selectedDevice;
                var name = $"{prefix}-{simDevice.ToString()}-{orientationString}";
                var guids = AssetDatabase.FindAssets(name);
                var first = guids.FirstOrDefault();

                if (first == default(string))
                {
                    throw new InvalidOperationException($"No mockup image named {name} in NotchSolution/Editor/Mockups folder!");
                }
                Sprite mockupSprite = AssetDatabase.LoadAssetAtPath<Sprite>(AssetDatabase.GUIDToAssetPath(first));

                foreach (var mockup in AllMockupCanvases)
                {
                    mockup.Show();
                    mockup.SetMockupSprite(
                        sprite: mockupSprite,
                        orientation: NotchSimulatorUtility.GetGameViewOrientation(),
                        simulate: enableSimulation,
                        flipped: NotchSimulatorUtility.flipOrientation
                    );
                }
            }
            else
            {
                foreach (var mockup in AllMockupCanvases)
                {
                    mockup.Hide();
                }
            }
        }

        private static void DebugTransitions(string s)
        {
#if NOTCH_SOLUTION_DEBUG_TRANSITIONS
            Debug.Log(s);
#endif
        }


        /// <param name="prefabStage">If not `null`, look for the mockup canvas on environment scene for editing a prefab **instead** of normal scenes.</param>
        private static void EnsureCanvasAndEventSetup(PrefabStage prefabStage = null)
        {
            //Create the hidden canvas if not already.
            bool prefabMode = prefabStage != null;
            var selectedMockupCanvas = prefabMode ? prefabMockupCanvas : mockupCanvas;

            if (selectedMockupCanvas == null)
            {
                //Find existing in the case of assembly reload
                //For some reason GameObject.FindObjectOfType could not get the canvas on main scene, it is active also, but by name works...
                var canvasObject = prefabMode ? prefabStage.stageHandle.FindComponentOfType<MockupCanvas>() : GameObject.Find(mockupCanvasName)?.GetComponent<MockupCanvas>();
                if (canvasObject != null)
                {
                    DebugTransitions($"[Notch Solution] Found existing (Prefab mode {prefabMode})");
                }
                else
                {
                    var prefabGuids = AssetDatabase.FindAssets(mockupCanvasName);
                    if(prefabGuids.Length == 0)
                    {
                        return;
                    }
                    DebugTransitions($"[Notch Solution] Creating canvas (Prefab mode {prefabMode})");
                    GameObject mockupCanvasPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(prefabGuids.First()));

                    var instantiated =
                    prefabMode ?
                    (GameObject)(PrefabUtility.InstantiatePrefab(mockupCanvasPrefab, prefabStage.scene)) :
                    (GameObject)PrefabUtility.InstantiatePrefab(mockupCanvasPrefab);

                    canvasObject = instantiated.GetComponent<MockupCanvas>();
                    instantiated.hideFlags = overlayCanvasFlag;

                    if (Application.isPlaying)
                    {
                        DontDestroyOnLoad(canvasObject);
                    }
                }


                if (prefabMode)
                {
                    canvasObject.PrefabStage = true;
                    prefabMockupCanvas = canvasObject;
                }
                else
                {
                    canvasObject.PrefabStage = false;
                    mockupCanvas = canvasObject;
                }

                if (eventAdded == false)
                {
                    eventAdded = true;

                    //Add clean up event.
                    EditorApplication.playModeStateChanged += PlayModeStateChangeAction;
                    // EditorSceneManager.sceneClosing += (a, b) =>
                    // {
                    //     DebugTransitions($"Scene closing {a} {b}");
                    // };
                    // EditorSceneManager.sceneClosed += (a) =>
                    // {
                    //     DebugTransitions($"Scene closed {a}");
                    // };
                    // EditorSceneManager.sceneLoaded += (a, b) =>
                    //  {
                    //      DebugTransitions($"Scene loaded {a} {b}");
                    //  };
                    // EditorSceneManager.sceneUnloaded += (a) =>
                    //  {
                    //      DebugTransitions($"Scene unloaded {a}");
                    //  };
                    PrefabStage.prefabStageOpened += (ps) =>
                    {
                        DebugTransitions($"Prefab opening {ps.scene.GetRootGameObjects().First().name} {ps.prefabContentsRoot.name}");

                        //On open prefab, the "dont save" objects on the main scene will disappear too.
                        //So that we could still see it in the game view WHILE editing a prefab, we make it back.
                        //Along with this the prefab mode canvas will also be updated.
                        UpdateAllMockups();

                        //On entering prefab mode, the Notch Simulator panel did not get OnGUI().
                        UpdateSimulatorTargets();
                    };

                    PrefabStage.prefabStageClosing += (ps) =>
                    {
                        DebugTransitions($"Prefab closing {ps.scene.GetRootGameObjects().First().name} {ps.prefabContentsRoot.name}");
                        //There is no problem on closing prefab stage, no need to restore the outer mockup.
                    };

                    EditorSceneManager.sceneOpening += (a, b) =>
                    {
                        DebugTransitions($"Scene opening {a} {b}");
                        DestroyHiddenCanvas();
                    };

                    EditorSceneManager.sceneOpened += (a, b) =>
                    {
                        DebugTransitions($"Scene opened {a} {b}");
                        UpdateAllMockups();
                    };

                    void PlayModeStateChangeAction(PlayModeStateChange state)
                    {
                        DebugTransitions($"Changed state PLAY {EditorApplication.isPlaying} PLAY or WILL CHANGE {EditorApplication.isPlayingOrWillChangePlaymode}");
                        switch (state)
                        {
                            case PlayModeStateChange.EnteredEditMode:
                                DebugTransitions($"Entered Edit {canvasObject}");
                                AddOverlayInPlayMode(); //For when coming back from play mode.
                                break;
                            case PlayModeStateChange.EnteredPlayMode:
                                DebugTransitions($"Entered Play {canvasObject}");
                                break;
                            case PlayModeStateChange.ExitingEditMode:
                                DebugTransitions($"Exiting Edit {canvasObject}");
                                DestroyHiddenCanvas();//Clean up the DontSave canvas we made in edit mode.
                                break;
                            case PlayModeStateChange.ExitingPlayMode:
                                DebugTransitions($"Exiting Play {canvasObject}");
                                DestroyHiddenCanvas();//Clean up the DontSave canvas we made in play mode.
                                break;
                        }
                    }

                }
            }

        }
    }
}
