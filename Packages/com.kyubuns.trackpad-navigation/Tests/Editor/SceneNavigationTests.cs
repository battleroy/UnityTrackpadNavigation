using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace TrackpadNavigation.Tests
{
    public sealed class SceneNavigationTests
    {
        [UnityTest]
        public IEnumerator OrbitRetainsAnchorUntilOptionOrPointerStartsANewOperation()
        {
            var previousWindow = EditorWindow.focusedWindow;
            var view = ScriptableObject.CreateInstance<SceneView>();
            Vector2 screen = default;
            int picks = 0;
            void ReadScreenPoint(SceneView current)
            {
                if (current == view && Event.current.type == EventType.Repaint)
                {
                    screen = GUIUtility.GUIToScreenPoint(HandleUtility.WorldToGUIPoint(view.pivot));
                }
                if (current == view && Event.current.type == EventType.ExecuteCommand &&
                    Event.current.commandName == "TrackpadNavigation.PickObject")
                {
                    ++picks;
                }
            }
            SceneView.duringSceneGui += ReadScreenPoint;
            try
            {
                view.Show();
                view.Focus();
                EditorMember.Set(EditorMember.Get(view, "overlayCanvas"), "overlaysEnabled", false);
                foreach (bool orthographic in new[]
                {
                    false,
                    true
                })
                {
                    view.LookAt(Vector3.zero, Quaternion.identity, 8, orthographic, instant: true);
                    picks = 0;
                    for (int frame = 0; frame < 5; ++frame)
                    {
                        view.Repaint();
                        yield return null;
                    }
                    Assert.That(screen, Is.Not.EqualTo(Vector2.zero));
                    var settings = new TrackpadPreferences();
                    var capture = TrackpadNavigator.CaptureFor(view, new NativePointer
                    {
                        X = screen.x, Y = screen.y, Active = 1, WindowNumber = 1
                    }, settings);
                    Assert.That(capture.Kinds & 1, Is.EqualTo(1));
                    var target = (SceneViewNavigation)typeof(TrackpadNavigator)
                        .GetField("target", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                    Assert.That(target.HitTest(screen - view.position.position), Is.True);
                    var input = new TrackpadEvent
                    {
                        Kind = GestureKind.Scroll, Phase = GesturePhase.Began,
                        Modifiers = GestureModifiers.Option, OptionSession = 1,
                        ScreenX = screen.x, ScreenY = screen.y, DeltaX = 20
                    };
                    target.Apply(input, settings);
                    Assert.That(picks, Is.EqualTo(1));
                    // GPU Pickingに依存せず、画面端の既知のPOIでカメラの回転と寿命を検証する。
                    var anchor = new Vector3(2, 1, 0);
                    typeof(SceneViewNavigation).GetField("orbitPoint", BindingFlags.Instance | BindingFlags.NonPublic)
                        .SetValue(target, anchor);
                    input.Phase = GesturePhase.Ended;
                    input.DeltaX = 0;
                    target.Apply(input, settings);
                    var rotation = view.rotation;
                    var pivot = view.pivot;
                    input.Phase = GesturePhase.None;
                    input.MomentumPhase = GesturePhase.Began;
                    input.DeltaX = 100;
                    target.Apply(input, settings);
                    Assert.That(view.rotation, Is.EqualTo(rotation));
                    Assert.That(view.pivot, Is.EqualTo(pivot));
                    input.MomentumPhase = GesturePhase.None;
                    input.Phase = GesturePhase.Began;
                    input.ScreenX += 10;
                    input.DeltaX = 20;
                    target.Apply(input, settings);
                    Assert.That(EditorMember.Get(target, "orbitPoint"), Is.EqualTo(anchor));
                    Assert.That(picks, Is.EqualTo(1));
                    var before = Quaternion.Inverse(rotation) * (anchor - pivot);
                    var after = Quaternion.Inverse(view.rotation) * (anchor - view.pivot);
                    Assert.That(Vector3.Distance(before, after), Is.LessThan(0.001f));

                    input.Phase = GesturePhase.Changed;
                    input.ScreenX += 150;
                    input.DeltaX = 0;
                    target.Apply(input, settings);
                    Assert.That(EditorMember.Get(target, "orbitPoint"), Is.EqualTo(anchor), "Cursor movement during a swipe must not change its anchor");
                    input.ScreenX -= 150;
                    input.Phase = GesturePhase.Began;

                    // カーソルが同じ場所でも、Optionを押し直したら現在の表面を拾い直す。
                    input.OptionSession = 3;
                    input.DeltaX = 0;
                    target.Apply(input, settings);
                    Assert.That(picks, Is.EqualTo(2), "A new Option press must pick again at the same cursor position");

                    input.ScreenX += 150;
                    input.DeltaX = 0;
                    target.Apply(input, settings);
                    Assert.That(picks, Is.EqualTo(3), "A distant cursor must pick again even while Option stays held");

                    input.Phase = GesturePhase.Cancelled;
                    target.Apply(input, settings);
                    Assert.That(EditorMember.Get(target, "orbitPoint"), Is.Null);
                    input.Phase = GesturePhase.Began;
                    target.Apply(input, settings);
                    input.Modifiers = GestureModifiers.Command | GestureModifiers.Option;
                    target.Apply(input, settings);
                    Assert.That(EditorMember.Get(target, "orbitPoint"), Is.Null);
                    TrackpadNavigator.ClearTarget();
                }
            }
            finally
            {
                SceneView.duringSceneGui -= ReadScreenPoint;
                TrackpadNavigator.ClearTarget();
                view.Close();
                if (previousWindow)
                {
                    previousWindow.Focus();
                }
            }
        }

        [Test]
        public void PanTracksScreenPointsAcrossDepthAndProjection()
        {
            var owner = new GameObject("Pan projection check", typeof(Camera))
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            try
            {
                var camera = owner.GetComponent<Camera>();
                var rotation = Quaternion.Euler(20, 30, 0);
                var delta = new Vector2(100, 50);
                foreach (bool orthographic in new[] {
                    false,
                    true
                })
                {
                    foreach (int pixelsPerPoint in new[] {
                        1,
                        2
                    })
                    {
                        foreach (float depth in new[] {
                            0.2f,
                            20f,
                            20000f
                        })
                        {
                            camera.orthographic = orthographic;
                            camera.orthographicSize = depth * 0.5f;
                            camera.fieldOfView = 55;
                            camera.pixelRect = new Rect(0, 0, 1200 * pixelsPerPoint, 800 * pixelsPerPoint);
                            camera.transform.SetPositionAndRotation(Vector3.zero, rotation);
                            var point = rotation * Vector3.forward * depth;
                            var before = camera.WorldToScreenPoint(point);
                            float viewportHeight = camera.pixelHeight / (float)pixelsPerPoint;
                            float units = NavigationMath.WorldUnitsPerPoint(camera, depth, viewportHeight);
                            camera.transform.position -= rotation * new Vector3(delta.x, -delta.y, 0) * units;
                            var moved = (Vector2)(camera.WorldToScreenPoint(point) - before) / pixelsPerPoint;
                            Assert.That(Vector2.Distance(moved, new Vector2(100, -50)), Is.LessThan(0.02f), $"ortho={orthographic}, dpi={pixelsPerPoint}, depth={depth}");
                        }
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(owner);
            }
        }

        [Test]
        public void ZoomKeepsOffCenterAnchorAtTheSameScreenPosition()
        {
            var owner = new GameObject("Zoom projection check", typeof(Camera))
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            try
            {
                var camera = owner.GetComponent<Camera>();
                var rotation = Quaternion.Euler(20, 30, 0);
                var pivot = new Vector3(2, 3, 4);
                foreach (bool orthographic in new[] {
                    false,
                    true
                })
                {
                    foreach (float ratio in new[] {
                        0.5f,
                        2f
                    })
                    {
                        camera.orthographic = orthographic;
                        camera.orthographicSize = 6;
                        camera.pixelRect = new Rect(0, 0, 1200, 800);
                        camera.transform.SetPositionAndRotation(pivot - rotation * Vector3.forward * 12, rotation);
                        var anchor = pivot + rotation * new Vector3(3, 2, orthographic ? 0 : -4);
                        var before = camera.WorldToScreenPoint(anchor);
                        var nextPivot = NavigationMath.ZoomPivot(pivot, anchor, ratio);
                        camera.orthographicSize *= ratio;
                        camera.transform.position = nextPivot - rotation * Vector3.forward * (orthographic ? 12 : 12 * ratio);
                        Assert.That(Vector2.Distance(before, camera.WorldToScreenPoint(anchor)), Is.LessThan(0.01f));
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(owner);
            }
        }

        [Test]
        public void OrbitPreservesRadiusAndOffCenterComposition()
        {
            var rotation = Quaternion.Euler(20, 30, 0);
            var nextRotation = NavigationMath.Rotate(rotation, new Vector2(35, -20), new TrackpadPreferences());
            var pivot = new Vector3(2, 3, 4);
            var poi = new Vector3(5, 1, -2);
            const float distance = 12;
            var camera = pivot - rotation * Vector3.forward * distance;
            var nextPivot = NavigationMath.OrbitPivot(pivot, poi, rotation, nextRotation);
            var nextCamera = nextPivot - nextRotation * Vector3.forward * distance;
            Assert.That(Vector3.Distance(nextCamera, poi), Is.EqualTo(Vector3.Distance(camera, poi)).Within(0.0001f));
            var before = Quaternion.Inverse(rotation) * (poi - camera);
            var after = Quaternion.Inverse(nextRotation) * (poi - nextCamera);
            Assert.That(Vector3.Distance(before, after), Is.LessThan(0.0001f), "POI must stay at the same screen position");
        }

        [Test]
        public void MeshPickingHandlesUnreadableSubmeshesAndNegativeScale()
        {
            var mesh = new Mesh
            {
                indexFormat = UnityEngine.Rendering.IndexFormat.UInt32
            };
            try
            {
                mesh.vertices = new[] {
                    new Vector3(-1, -1, 0),
                    new Vector3(1, -1, 0),
                    new Vector3(0, 1, 0),
                    new Vector3(-1, -1, 2),
                    new Vector3(1, -1, 2),
                    new Vector3(0, 1, 2)
                };
                mesh.subMeshCount = 2;
                mesh.SetIndices(new[] {
                    0,
                    1,
                    2
                }, MeshTopology.Triangles, 0);
                mesh.SetIndices(new[] {
                    0,
                    1,
                    2
                }, MeshTopology.Triangles, 1, calculateBounds: true, baseVertex: 3);
                mesh.UploadMeshData(markNoLongerReadable: true);
                var matrix = Matrix4x4.TRS(new Vector3(1, 2, 3), Quaternion.Euler(20, 30, 10), new Vector3(-2, 3, 4));
                var ray = new Ray(matrix.MultiplyPoint3x4(new Vector3(0, 0, -5)), matrix.MultiplyVector(Vector3.forward).normalized);
                Assert.That(ScenePicking.RaycastMesh(mesh, matrix, ray, out var point), Is.True);
                Assert.That(Vector3.Distance(point, matrix.MultiplyPoint3x4(Vector3.zero)), Is.LessThan(0.0001f));
                ray.origin = matrix.MultiplyPoint3x4(new Vector3(10, 0, -5));
                Assert.That(ScenePicking.RaycastMesh(mesh, matrix, ray, out _), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }
    }
}
