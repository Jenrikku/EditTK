﻿using EditTK.Cameras;
using EditTK.Core.Utils;
using EditTK.Drawing;
using EditTK.Graphics;
using EditTK.Graphics.Helpers;
using EditTK.Util;
using EditTK.Windowing;
using ImGuiNET;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Veldrid;
using Veldrid.ImageSharp;
using Veldrid.OpenGLBinding;
using Veldrid.StartupUtilities;

using static EditTK.Graphics.GraphicsAPI;
using static EditTK.Graphics.VeldridUtils;
using static EditTK.Input.InputTracker;

namespace EditTK.Testing
{

    class TestWindow : VeldridSDLWindow
    {
        struct SceneUB
        {
            public Matrix4x4 View;
            public Vector3 CamPlaneNormal;
            public float CamPlaneOffset;
            public Vector2 ViewportSize;
            public float ForceSolidHighlight;
            public float BlendAlpha;

            public SceneUB(Matrix4x4 view, Vector3 camPlaneNormal, float camPlaneOffset, Vector2 viewportSize, float forceSolidHighlight, float blendAlpha)
            {
                View = view;
                CamPlaneNormal = camPlaneNormal;
                CamPlaneOffset = camPlaneOffset;
                ViewportSize = viewportSize;
                ForceSolidHighlight = forceSolidHighlight;
                BlendAlpha = blendAlpha;
            }
        }

        public struct InstanceWithID
        {
            [VertexAttributeAtrribute("Transform", VertexElementFormat.Float4, 4)]
            public Matrix4x4 Transform;

            [VertexAttributeAtrribute("Id", VertexElementFormat.UInt1)]
            public uint Id;

            public InstanceWithID(Matrix4x4 transform, uint id)
            {
                Transform = transform;
                Id = id;
            }
        }


        private readonly GizmoDrawer _gizmoDrawer;
        private readonly ImageSharpTexture orientationCubeTexture;
        private readonly ShaderUniformLayout _sceneUniformLayout;
        private readonly ShaderUniformLayout _compositeUniformLayout;
        private readonly ShaderUniformLayout _planeUniformLayout;

        private RgbaByte _pickedColor = RgbaByte.Clear;

        private float rotX = 0.5f;
        private float rotY = 0.4f;
        private float targetDistance = 10;

        private readonly GenericModel<int, VertexPositionTexture> _planeModel;
        private readonly GenericModel<int, VertexPositionColor> _cubeModel;
        private readonly GenericInstanceHolder<InstanceWithID> _cubeInstances;

        private readonly GenericModelRenderer<int, VertexPositionTexture> _planeRenderer;
        private readonly GenericInstanceRenderer<int, VertexPositionColor, InstanceWithID> _cubeRenderer;
        private readonly ComputeShader _compositeShader;

        private readonly SimpleFrameBuffer _sceneFB = new SimpleFrameBuffer(
            PixelFormat.D32_Float_S8_UInt,

            PixelFormat.R8_G8_B8_A8_UNorm,
            PixelFormat.R32_UInt);

        private readonly PixelReader<RgbaByte> _colorPixelReader;
        private readonly PixelReader<uint> _pidPixelReader;
        private PixelReader<float> _depthPixelReader;
        private DeviceBuffer? _sceneUB;
        private ResourceSet? _sceneSet;
        private ResourceSet _compositeSet;
        private DeviceBuffer? _planeUB;
        private ResourceSet? _planeSet;

        private Texture? _finalTexture;

        private float _far = 1000;
        private float _pickedDepth = 0;
        private float _fps;
        private float _displayFps;
        private readonly SimpleCam _cam = new();


        private readonly string CheckerPlane_VertexCode =
            File.ReadAllText(SystemUtils.RelativeFilePath("shaders", "checkerPlane.vert"));

        private readonly string CheckerPlane_FragmentCode =
            File.ReadAllText(SystemUtils.RelativeFilePath("shaders", "checkerPlane.frag"));


        private readonly string ArrowCube_VertexCode =
            File.ReadAllText(SystemUtils.RelativeFilePath("shaders", "arrowCube.vert"));

        private readonly string ArrowCube_FragmentCode =
            File.ReadAllText(SystemUtils.RelativeFilePath("shaders", "arrowCube.frag"));


        private readonly string Composition_ComputeCode =
            File.ReadAllText(SystemUtils.RelativeFilePath("shaders", "composition.comp"));


        Matrix4x4[] _testTransforms = new[]
        {
            Matrix4x4.CreateTranslation(0, 0, 0),
            Matrix4x4.CreateTranslation(-3, 2, -4),
            Matrix4x4.CreateTranslation(3, 0, -3),
        };


        private string _givenWindowTitle;
        private bool _isDragging;
        private bool _stressTest;
        private uint _pickedId;

        public TestWindow(WindowCreateInfo wci) : base(wci)
        {
            _givenWindowTitle = Title;

            TimeTracker.AddIntervallHandler(x => _displayFps = _fps, 0.1);



            _gizmoDrawer = new GizmoDrawer(new Vector2(Width, Height), _cam);

            orientationCubeTexture = new ImageSharpTexture(SystemUtils.RelativeFilePath("Resources","OrientationCubeTex.png"));


            _colorPixelReader = new PixelReader<RgbaByte>(PixelFormat.R8_G8_B8_A8_UNorm);
            _pidPixelReader = new PixelReader<uint>(PixelFormat.R32_UInt);
            _depthPixelReader = new PixelReader<float>(PixelFormat.D32_Float_S8_UInt);

            _sceneUniformLayout = ShaderUniformLayoutBuilder.Get()
                .BeginUniformBuffer("ub_Scene", ShaderStages.Vertex | ShaderStages.Fragment)
                    .AddUniform<Matrix4x4>("View")
                    .AddUniform<Vector3>("CamPlaneNormal")
                    .AddUniform<float>("CamPlaneOffset")
                    .AddUniform<Vector2>("ViewportSize")
                    .AddUniform<float>("ForceSolidHighlight")
                    .AddUniform<float>("BlendAlpha")
                .EndUniformBuffer()
                .GetLayout();

            _compositeUniformLayout = ShaderUniformLayoutBuilder.Get()
                .AddResourceUniform("ub_Scene", ResourceKind.UniformBuffer, ShaderStages.Compute)
                .AddResourceUniform("OutputTexture", ResourceKind.TextureReadWrite, ShaderStages.Compute)
                .AddTexture("Color0",        ShaderStages.Compute)
                .AddTexture("Depth0",        ShaderStages.Compute)
                .AddSampler("LinearSampler", ShaderStages.Compute)
                .AddTexture("Picking0",      ShaderStages.Compute)
                .AddSampler("PointSampler",  ShaderStages.Compute)
                .GetLayout();

            _planeUniformLayout = ShaderUniformLayoutBuilder.Get()
                .BeginUniformBuffer("ub_Plane", ShaderStages.Vertex | ShaderStages.Fragment)
                    .AddUniform<Matrix4x4>("Transform")
                .EndUniformBuffer()
                .GetLayout();


            #region Plane Model
            {
                var builder = new GenericModelBuilder<VertexPositionTexture>();

                builder.AddPlane(
                    new VertexPositionTexture(new Vector3(-100, 0, -100), Vector2.Zero),
                    new VertexPositionTexture(new Vector3(100, 0, -100), Vector2.Zero),
                    new VertexPositionTexture(new Vector3(-100, 0, 100), Vector2.Zero),
                    new VertexPositionTexture(new Vector3(100, 0, 100), Vector2.Zero)
                    );

                _planeModel = builder.GetModel();
            }
            #endregion


            #region Cube Model
            {

                var builder = new GenericModelBuilder<VertexPositionColor>();

                float BEVEL = 0.2f;

                Vector4 defaultColor = new Vector4(0, 0, 0, 1);
                Vector4 lineColor    = new Vector4(1, 1, 1, 1);

                Matrix4x4 mtx;

                #region Transform Helpers
                void Reset() => mtx = Matrix4x4.CreateScale(0.5f);

                static void Rotate(ref float x, ref float y)
                {
                    var _x = x;
                    x = y;
                    y = -_x;
                }

                void RotateOnX()
                {
                    Rotate(ref mtx.M12, ref mtx.M13);
                    Rotate(ref mtx.M22, ref mtx.M23);
                    Rotate(ref mtx.M32, ref mtx.M33);
                }

                void RotateOnY()
                {
                    Rotate(ref mtx.M11, ref mtx.M13);
                    Rotate(ref mtx.M21, ref mtx.M23);
                    Rotate(ref mtx.M31, ref mtx.M33);
                }

                void RotateOnZ()
                {
                    Rotate(ref mtx.M11, ref mtx.M12);
                    Rotate(ref mtx.M21, ref mtx.M22);
                    Rotate(ref mtx.M31, ref mtx.M32);
                }

                #endregion

                float w = 1 - BEVEL;
                float m = 1 - BEVEL * 0.3f;


                #region Cube part Helpers
                void Face()
                {
                    builder!.AddPlane(
                        new(Vector3.Transform(new Vector3(-w, 1, -w), mtx), defaultColor),
                        new(Vector3.Transform(new Vector3( w, 1, -w), mtx), defaultColor),
                        new(Vector3.Transform(new Vector3(-w, 1,  w), mtx), defaultColor),
                        new(Vector3.Transform(new Vector3( w, 1,  w), mtx), defaultColor)
                    );
                }

                void Bevel()
                {
                    builder!.AddPlane(
                        new(Vector3.Transform(new Vector3(-w, 1, w), mtx), defaultColor),
                        new(Vector3.Transform(new Vector3( w, 1, w), mtx), defaultColor),
                        new(Vector3.Transform(new Vector3(-w, m, m), mtx), lineColor),
                        new(Vector3.Transform(new Vector3( w, m, m), mtx), lineColor)
                    );

                    builder!.AddPlane(
                        new(Vector3.Transform(new Vector3(-w, m, m), mtx), lineColor),
                        new(Vector3.Transform(new Vector3( w, m, m), mtx), lineColor),
                        new(Vector3.Transform(new Vector3(-w, w, 1), mtx), defaultColor),
                        new(Vector3.Transform(new Vector3( w, w, 1), mtx), defaultColor)
                    );
                }

                void BevelCorner()
                {
                    void Piece(Vector3 v1, Vector3 v2, Vector3 v3)
                    {
                        Vector3 vm = new(m, m, m);

                        builder!.AddTriangle(
                            new(Vector3.Transform(v1, mtx), defaultColor),
                            new(Vector3.Transform(v2, mtx), lineColor),
                            new(Vector3.Transform(vm, mtx), lineColor)
                        );

                        builder!.AddTriangle(
                            new(Vector3.Transform(v2, mtx), lineColor),
                            new(Vector3.Transform(v3, mtx), defaultColor),
                            new(Vector3.Transform(vm, mtx), lineColor)
                        );
                    }

                    Piece(new Vector3(w, w, 1), new Vector3(w, m, m), new Vector3(w, 1, w));
                    Piece(new Vector3(w, 1, w), new Vector3(m, m, w), new Vector3(1, w, w));
                    Piece(new Vector3(1, w, w), new Vector3(m, w, m), new Vector3(w, w, 1));
                }
                #endregion


                #region Construction

                Reset();

                #region Faces
                Face();
                RotateOnX();

                for (int i = 0; i < 4; i++)
                {
                    Face();
                    RotateOnY();
                }
                RotateOnX();
                Face();
                #endregion

                Reset();

                #region Edges/Bevels
                for (int i = 0; i < 3; i++)
                {
                    for (int j = 0; j < 4; j++)
                    {
                        Bevel();
                        RotateOnY();
                    }

                    RotateOnZ();
                }
                #endregion

                Reset();

                #region Corners/BevelCorners
                for (int i = 0; i < 2; i++)
                {
                    for (int j = 0; j < 4; j++)
                    {
                        BevelCorner();
                        RotateOnY();
                    }

                    RotateOnZ();
                }
                #endregion

                #endregion


                _cubeModel = builder.GetModel();

            }
            #endregion



            _cubeInstances = new GenericInstanceHolder<InstanceWithID>();



            _planeRenderer = new GenericModelRenderer<int, VertexPositionTexture>(
                vertexShaderBytes: Encoding.UTF8.GetBytes(CheckerPlane_VertexCode),
                fragmentShaderBytes: Encoding.UTF8.GetBytes(CheckerPlane_FragmentCode),
                uniformLayouts: new[]
                {
                    _sceneUniformLayout,
                    _planeUniformLayout
                },
                outputDescription: _sceneFB.OutputDescription,
                blendState: new BlendStateDescription(RgbaFloat.White, 
                    BlendAttachmentDescription.AlphaBlend,
                    BlendAttachmentDescription.Disabled)
                );

            _cubeRenderer = new GenericInstanceRenderer<int, VertexPositionColor, InstanceWithID>(
                vertexShaderBytes: Encoding.UTF8.GetBytes(ArrowCube_VertexCode),
                fragmentShaderBytes: Encoding.UTF8.GetBytes(ArrowCube_FragmentCode),
                uniformLayouts: new[]
                {
                    _sceneUniformLayout,
                    _planeUniformLayout
                },
                outputDescription: _sceneFB.OutputDescription,
                blendState: new BlendStateDescription(RgbaFloat.White,
                    BlendAttachmentDescription.AlphaBlend,
                    BlendAttachmentDescription.Disabled)
                );

            _compositeShader = new ComputeShader(
                computeShaderBytes: Encoding.UTF8.GetBytes(Composition_ComputeCode),
                uniformLayouts: new[]
                {
                    _compositeUniformLayout
                },
                8, 8, 1
                );
        }

        protected override void CreateResources(ResourceFactory factory)
        {
            _cam.SetProjectionMatrix(CreatePerspective(GD!, false, 1f, Width / (float)Height, 0.01f, 1000f));

            var deviceTex = orientationCubeTexture.CreateDeviceTexture(GD, factory);

            

            GizmoDrawer.SetOrientationCubeTexture(ImGuiRenderer.GetOrCreateImGuiBinding(factory,
                deviceTex));

            _sceneUB = CreateUniformBuffer(new SceneUB());

            _planeUB = CreateUniformBuffer(Matrix4x4.Identity);

            _planeSet = _planeUniformLayout.CreateResourceSet(_planeUB);

            Title = $"{_givenWindowTitle} - {GD!.BackendType}";
        }

        protected override void HandleWindowResize()
        {
            base.HandleWindowResize();

            _cam.SetProjectionMatrix(CreatePerspective(GD!, false, 1f, Width / (float)Height, 0.01f, _far));

            _gizmoDrawer.UpdateScreenSize(new Vector2(Width, Height));

            _sceneFB.SetSize(Width, Height);

            

            _finalTexture = ResourceFactory.CreateTexture(TextureDescription.Texture2D(
                Width, Height, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Sampled | TextureUsage.Storage));


            _sceneSet = _sceneUniformLayout.CreateResourceSet(_sceneUB!);

            _compositeSet = _compositeUniformLayout.CreateResourceSet(_sceneUB!, _finalTexture, 
                ResourceFactory.CreateTextureView(_sceneFB.ColorTextures[0]),
                ResourceFactory.CreateTextureView(_sceneFB.DepthTexture),
                GD!.LinearSampler,
                ResourceFactory.CreateTextureView(_sceneFB.ColorTextures[1]),
                GD!.PointSampler);
        }

        protected override void Draw(float deltaSeconds, CommandList cl)
        {
            var dl = ImGui.GetBackgroundDrawList();


            var rotMtx = Matrix4x4.CreateRotationY(rotY) * Matrix4x4.CreateRotationX(rotX);

            _cam.SetViewMatrix(rotMtx * Matrix4x4.CreateTranslation(0, 0, -targetDistance));


            if (GD!.IsUvOriginTopLeft)
            {
                dl.AddImage(
                ImGuiRenderer.GetOrCreateImGuiBinding(GraphicsAPI.ResourceFactory, _finalTexture),
                new Vector2(0, 0), new Vector2(Width, Height));
            }
            else
            {
                dl.AddImage(
                ImGuiRenderer.GetOrCreateImGuiBinding(GraphicsAPI.ResourceFactory, _finalTexture),
                new Vector2(0, Height), new Vector2(Width, 0));
            }


            #region Test UI
            bool imguiHovered = false;


            ImGui.GetStyle().WindowPadding = new Vector2(30, 20);
            ImGui.GetStyle().WindowRounding = 0;
            ImGui.GetStyle().FrameRounding = 5;


            ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg] = new Vector4(0, 0, 0, 0.2f);

            #region Side Bar
            ImGui.SetNextWindowPos(new Vector2(0, 0));
            ImGui.SetNextWindowSize(new Vector2(200, Height));

            ImGui.GetStyle().Colors[(int)ImGuiCol.Button] = new Vector4(0.3f, 0.3f, 0.3f, 0.4f);
            ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonHovered] = new Vector4(0.4f, 0.5f, 0.6f, 0.4f);

            ImGui.Begin("Side", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove);

            _fps = 1 / deltaSeconds;
            ImGui.Text($"fps: {_displayFps,0:F0}");


            //for (int i = 0; i < buttonCount; i++)
            //{
            //    ImGui.PushID(i);
            //    if (ImGui.Button("Test", new Vector2(140, 30)))
            //        buttonCount++;
            //    ImGui.PopID();
            //}

            ImGui.Dummy(new Vector2(0, 20));

            
            
            ImGui.SetWindowFontScale(1.1f);
            ImGui.Text("Camera");
            ImGui.SetWindowFontScale(1f);

            ImGui.Text($"  Rotation X: {MathUtils.RADIANS_TO_DEGREES * rotX,0:F0}°");
            ImGui.Text($"  Rotation Y: {MathUtils.RADIANS_TO_DEGREES * rotY,0:F0}°");
            ImGui.Text($"  Distance: {targetDistance}m");

            ImGui.Dummy(new Vector2(0, 10));
            ImGui.Separator();

            if (Hovered && !ImGui.IsWindowHovered())
            {
                ImGui.Dummy(new Vector2(0, 10));

                ImGui.SetWindowFontScale(1.1f);
                ImGui.Text("Pixel under Cursor");
                ImGui.SetWindowFontScale(1f);
                ImGui.Dummy(new Vector2(0, 5));

                ImGui.Text("  Color:");
                ImGui.SameLine();
                Vector2 topLeft = ImGui.GetWindowPos() + ImGui.GetCursorPos();

                topLeft.X += 5;

                uint col = 0;

                col |= _pickedColor.A; col <<= 8;
                col |= _pickedColor.B; col <<= 8;
                col |= _pickedColor.G; col <<= 8;
                col |= _pickedColor.R;

                var size = new Vector2(20, 20);

                topLeft += new Vector2(1);
                ImGui.GetWindowDrawList().AddRectFilled(topLeft, topLeft+size,
                    0x55_00_00_00, 5);

                topLeft -= new Vector2(2);
                ImGui.GetWindowDrawList().AddRectFilled(topLeft, topLeft + size,
                    col, 5);

                ImGui.Dummy(size);

                ImGui.Dummy(new Vector2(0, 5));

                ImGui.Text($"  Depth:   {_pickedDepth,0:F2}m");

                ImGui.Text($"  Id:   0x{_pickedId:X6}");

                
            }
            imguiHovered |= ImGui.IsAnyItemHovered() || ImGui.IsWindowHovered();

            ImGui.End();
            #endregion

            #region Top Bar
            ImGui.SetNextWindowPos(new Vector2(200,0));
            ImGui.SetNextWindowSize(new Vector2(Width-200, 80));

            ImGui.GetStyle().Colors[(int)ImGuiCol.Button] = new Vector4(0.3f, 0.3f, 0.3f, 0.2f);
            ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonHovered] = new Vector4(0.4f, 0.5f, 0.6f, 0.4f);

            ImGui.Begin("Top", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollWithMouse);

            {
                Vector2 topLeft = new Vector2(Width / 2 - 380/2 - 200, 0);

                bool isOpenGL = GD.BackendType == GraphicsBackend.OpenGL;
                bool isMultipleWindows = WindowManager.MoreThanOneWindow();

                ImGui.GetWindowDrawList().AddText(
                ImGui.GetWindowPos() + topLeft + new Vector2(-40, 10),
                0x99_ff_ff_ff, "Use buttons to switch APIs.     Note: OpenGL does not support mutliple Windows!");



                #region Direct3D
                ImGui.SetCursorPos(topLeft+new Vector2(0, 40));
                if (ImGui.Button("Direct3D"))
                    WindowManager.RequestBackend(GraphicsBackend.Direct3D11);
                #endregion

                #region OpenGL
                ImGui.SetCursorPos(topLeft + new Vector2(100, 40));

                ImGui.BeginDisabled(isMultipleWindows);
                if (ImGui.Button("OpenGL"))
                    WindowManager.RequestBackend(GraphicsBackend.OpenGL);
                ImGui.EndDisabled();
                #endregion

                #region Vulkan
                ImGui.SetCursorPos(topLeft + new Vector2(200, 40));
                if (ImGui.Button("Vulkan"))
                    WindowManager.RequestBackend(GraphicsBackend.Vulkan);
                #endregion


                #region New Window
                ImGui.BeginDisabled(isOpenGL);
                ImGui.SetCursorPos(topLeft + new Vector2(300, 40));
                if (ImGui.Button("New Window"))
                    new TestWindow(new WindowCreateInfo(
                        100, 100, (int)Width, (int)Height, WindowState.Normal, "New Window")).Run();
                ImGui.EndDisabled();
                #endregion

                #region New Window
                ImGui.SetCursorPos(topLeft + new Vector2(600, 40));
                if (ImGui.Button("Toogle Stress Test: "+(_stressTest?"On":"Off")))
                    _stressTest = !_stressTest;
                #endregion
            }


            imguiHovered |= ImGui.IsAnyItemHovered();
            ImGui.End();
            #endregion


            #endregion

            #region Gizmos

            bool viewHovered = Hovered && !imguiHovered;

            if (viewHovered && GetMouseButtonDown(MouseButton.Right))
                _isDragging = true;

            if (!GetMouseButton(MouseButton.Right))
                _isDragging = false;

            _gizmoDrawer.BeginFrame(dl);

            if (_isDragging)
            {
                rotY += MouseMoveDelta.X * 0.005f;
                rotX += MouseMoveDelta.Y * 0.005f;
            }

            if(viewHovered)
                targetDistance -= MouseWheelDelta;

            if (_stressTest)
            {
                _gizmoDrawer.TranslationGizmo(Matrix4x4.CreateTranslation(0, 0, 0), 64, out _);
            }
            else
            {
                _gizmoDrawer.RotationGizmo(_testTransforms[0], 64, out _);

                _gizmoDrawer.ScaleGizmo(_testTransforms[1], 64, out _);

                _gizmoDrawer.TranslationGizmo(_testTransforms[2], 64, out _);
            }


            if (_gizmoDrawer.OrientationCube(new Vector2(Width - 100, Height - 100), 50, out Vector3 hoveredFacingDirection) && 
                Input.InputTracker.GetMouseButtonDown(MouseButton.Left))
            {
                hoveredFacingDirection = Vector3.Normalize(hoveredFacingDirection);

                rotY = (float)Math.Atan2(-hoveredFacingDirection.X, hoveredFacingDirection.Z);
                rotX = (float)Math.Asin(hoveredFacingDirection.Y);

                if (hoveredFacingDirection.X == 0 && hoveredFacingDirection.Z == 0)
                    rotY = 0;
            }
            #endregion


            #region Scene Rendering
            Vector3 camPlaneNormal = -_cam.ForwardVector / _far;

            cl.UpdateBuffer(_sceneUB, 0, new SceneUB(
                _cam.ViewMatrix * _cam.ProjectionMatrix,
                camPlaneNormal, Vector3.Dot(camPlaneNormal, _cam.Position),
                new Vector2(Width, Height),
                0, 0
                ));


            float baseScale = _stressTest ? 3.75f : 0.75f;

            cl.UpdateBuffer(_planeUB, 0,
                Matrix4x4.CreateScale((float)(baseScale + Math.Sin(TimeTracker.Time) * 0.25)));


            _sceneFB.Use(cl);
            cl.ClearColorTarget(0, new RgbaFloat(0, 0, 0.2f, 1));
            cl.ClearColorTarget(1, RgbaFloat.Clear);
            cl.ClearDepthStencil(1f);


            cl.InsertDebugMarker("drawing plane");
            _planeRenderer.Draw(cl, _planeModel, _sceneSet!, _planeSet!);


            cl.UpdateBuffer(_planeUB, 0,
                Matrix4x4.CreateScale(0.99f)*
                Matrix4x4.CreateRotationY((float)(TimeTracker.Time * 0.1))*
                Matrix4x4.CreateRotationX((float)(TimeTracker.Time * 0.1))*
                Matrix4x4.CreateTranslation(0,2,0));


            _cubeInstances.Clear();



            void Cube(Matrix4x4 transform, uint id)
            {
                if (id == _pickedId)
                    transform = Matrix4x4.CreateScale(1.1f) * transform;

                _cubeInstances.Add(new InstanceWithID(transform, id));
            }

            if (_stressTest)
            {
                //120,000 cubes

                for (int i = 0; i < 300; i++)
                {
                    for (int j = 0; j < 400; j++)
                    {
                        Matrix4x4 mtx =
                            Matrix4x4.CreateScale(Math.Max(0, (float)Math.Sin(TimeTracker.Time + i * j))) *
                            Matrix4x4.CreateTranslation(300 - i * 2, 0, 400 - j * 2);


                        uint id = (uint)(i + 300 * j);


                        Cube(mtx,2+id);


                    }
                }
            }
            else
            {
                

                Cube(_testTransforms[0],2);
                Cube(_testTransforms[1],3);
                Cube(_testTransforms[2],4);
            }
            



            _cubeRenderer.Draw(cl, _cubeModel, _cubeInstances, _sceneSet!, _planeSet!);


            cl.SetFramebuffer(MainSwapchain.Framebuffer);

            _compositeShader.Dispatch(cl,
                (uint)Math.Ceiling(Width / 16f),
                (uint)Math.Ceiling(Height / 16f), 1,
                _compositeSet!);



            
            if (Hovered && WindowHovered)
            {
                _colorPixelReader.ReadPixel(cl, _finalTexture!, (uint)MousePosition.X, (uint)MousePosition.Y, 
                    CommandListFence,
                    pixel => _pickedColor = pixel);

                _depthPixelReader.ReadPixel(cl, _sceneFB.DepthTexture!, (uint)MousePosition.X, (uint)MousePosition.Y,
                    CommandListFence,
                    pixel => _pickedDepth = pixel * _far);

                _pidPixelReader.ReadPixel(cl, _sceneFB.ColorTextures[1]!, (uint)MousePosition.X, (uint)MousePosition.Y,
                    CommandListFence,
                    pixel => _pickedId = pixel);

            }

            #endregion
        }
    }
}
