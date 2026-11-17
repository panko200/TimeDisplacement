using SharpGen.Runtime;
using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Player.Video.Effects;
using YukkuriMovieMaker.Plugin.Brush;

namespace TimeDisplacement
{
    internal sealed class TimeDisplacementEffectProcessor : VideoEffectProcessorBase
    {
        private static readonly Matrix4x4 PerspectiveMatrix = new(
            1f, 0f, 0f, 0f,
            0f, 1f, 0f, 0f,
            0f, 0f, 1f, -0.001f,
            0f, 0f, 0f, 1f);

        private readonly IGraphicsDevicesAndContext _devices;
        private readonly TimeDisplacementEffect _item;

        private TimeDisplacementD3DRenderer? _renderer;
        private AffineTransform2D? _zoomEffect;
        private Crop? _cropEffect;
        private Transform3D? _renderEffect;
        private ID2D1Image? _renderOutput;
        private AffineTransform2D? _centeringEffect;
        private ColorMatrix? _opacityEffect;
        private AffineTransform2D? _outputEffect;

        private IBrushSource? _brushSource;
        private Type? _lastBrushType;
        private Type? _cachedSceneParamType;
        private PropertyInfo? _cachedSceneIdProp;

        public TimeDisplacementEffectProcessor(IGraphicsDevicesAndContext devices, TimeDisplacementEffect item)
            : base(devices)
        {
            _devices = devices;
            _item = item;
        }

        protected override ID2D1Image? CreateEffect(IGraphicsDevicesAndContext devices)
        {
            _renderer = new TimeDisplacementD3DRenderer(devices);
            if (!_renderer.IsInitialized)
            {
                _renderer.Dispose();
                _renderer = null;
                return null;
            }
            disposer.Collect(_renderer);

            var dc = devices.DeviceContext;

            _zoomEffect = new AffineTransform2D(dc);
            disposer.Collect(_zoomEffect);

            _cropEffect = new Crop(dc);
            disposer.Collect(_cropEffect);

            _renderEffect = new Transform3D(dc);
            disposer.Collect(_renderEffect);

            _centeringEffect = new AffineTransform2D(dc);
            disposer.Collect(_centeringEffect);

            _opacityEffect = new ColorMatrix(dc);
            disposer.Collect(_opacityEffect);

            _outputEffect = new AffineTransform2D(dc);
            disposer.Collect(_outputEffect);

            using (var zoomOut = _zoomEffect.Output)
                _cropEffect.SetInput(0, zoomOut, true);
            using (var cropOut = _cropEffect.Output)
                _renderEffect.SetInput(0, cropOut, true);
            using (var renderOut = _renderEffect.Output)
                _centeringEffect.SetInput(0, renderOut, true);
            using (var centerOut = _centeringEffect.Output)
                _opacityEffect.SetInput(0, centerOut, true);

            _renderOutput = _renderEffect.Output;
            disposer.Collect(_renderOutput);

            var output = _outputEffect.Output;
            disposer.Collect(output);
            return output;
        }

        protected override void setInput(ID2D1Image? input)
        {
            _zoomEffect?.SetInput(0, input, true);
        }

        protected override void ClearEffectChain()
        {
            _zoomEffect?.SetInput(0, null, true);
            _cropEffect?.SetInput(0, null, true);
            _renderEffect?.SetInput(0, null, true);
            _centeringEffect?.SetInput(0, null, true);
            _opacityEffect?.SetInput(0, null, true);
            _outputEffect?.SetInput(0, null, true);
        }

        public override DrawDescription Update(EffectDescription desc)
        {
            if (IsPassThroughEffect ||
                input == null ||
                _renderer == null ||
                _zoomEffect == null ||
                _cropEffect == null ||
                _renderEffect == null ||
                _renderOutput == null ||
                _centeringEffect == null ||
                _opacityEffect == null ||
                _outputEffect == null)
            {
                return desc.DrawDescription;
            }

            int frame = Math.Max(0, desc.ItemPosition.Frame);
            int duration = Math.Max(1, desc.ItemDuration.Frame);
            int fps = Math.Max(1, desc.FPS);
            int width = Math.Max(1, desc.ScreenSize.Width);
            int height = Math.Max(1, desc.ScreenSize.Height);

            float maxTime = (float)_item.MaxDisplacementTime.GetValue(frame, duration, fps);
            float timeResolution = (float)Math.Max(0.1, _item.TimeResolution.GetValue(frame, duration, fps));
            bool stretchMap = _item.StretchMapToFit;
            var shiftMode = _item.TimeShiftMode;
            bool enableBlending = _item.EnableFrameBlending;
            bool showMap = _item.IsMapPreviewEnabled;

            double peakMaxTime = Math.Abs((double)maxTime);
            foreach (var v in _item.MaxDisplacementTime.ActiveValues)
            {
                double absVal = Math.Abs(v.Value);
                if (absVal > peakMaxTime)
                    peakMaxTime = absVal;
            }

            if (!_renderer.EnsureResources(width, height, peakMaxTime, fps, shiftMode) ||
                _renderer.CurrentFrameBitmap == null ||
                _renderer.MapBitmap == null ||
                _renderer.OutputBitmap == null)
            {
                _outputEffect.SetInput(0, input, true);
                _outputEffect.TransformMatrix = Matrix3x2.Identity;
                return desc.DrawDescription;
            }

            var dc = _devices.DeviceContext;
            var drawDesc = desc.DrawDescription;

            RenderCurrentInput(dc, drawDesc, width, height, _renderer.CurrentFrameBitmap);
            RenderDisplacementMap(dc, desc, width, height, stretchMap, _renderer.MapBitmap);

            _renderer.RenderFrame(
                frame,
                fps,
                maxTime,
                timeResolution,
                shiftMode,
                enableBlending,
                showMap);

            _outputEffect.SetInput(0, _renderer.OutputBitmap, true);
            _outputEffect.TransformMatrix = Matrix3x2.CreateTranslation(-width / 2f, -height / 2f);

            return drawDesc with
            {
                Draw = Vector3.Zero,
                Zoom = new Vector2(1f, 1f),
                Rotation = Vector3.Zero,
                Camera = Matrix4x4.Identity,
                Opacity = 1.0,
                Invert = false,
                PerspectiveDistance = null,
                Billboard = BillboardMode.None,
            };
        }

        private void RenderCurrentInput(
            ID2D1DeviceContext6 dc,
            DrawDescription drawDesc,
            int width,
            int height,
            ID2D1Bitmap1 targetBitmap)
        {
            var zoom = drawDesc.Zoom;
            double opacity = drawDesc.Opacity;

            bool isInvisible = !float.IsFinite(zoom.X) || !float.IsFinite(zoom.Y) ||
                               zoom.X == 0f || zoom.Y == 0f ||
                               !double.IsFinite(opacity) || opacity <= 0.0;

            var oldTarget = dc.Target;
            var oldTransform = dc.Transform;
            try
            {
                dc.Target = targetBitmap;
                dc.Transform = Matrix3x2.Identity;
                dc.BeginDraw();
                dc.Clear(new Color4(0f, 0f, 0f, 0f));

                if (!isInvisible)
                {
                    _zoomEffect!.TransformMatrix =
                        (drawDesc.Invert ? Matrix3x2.CreateScale(-1f, 1f, drawDesc.CenterPoint) : Matrix3x2.Identity) *
                        Matrix3x2.CreateScale(zoom);
                    _zoomEffect.InterPolationMode = drawDesc.ZoomInterpolationMode.ToTransform2D();

                    Matrix4x4 camera = ApplyCameraAdjustments(drawDesc);
                    float d2r = (float)(Math.PI / 180.0);
                    Matrix4x4 world =
                        Matrix4x4.CreateRotationZ(drawDesc.Rotation.Z * d2r) *
                        Matrix4x4.CreateRotationY(-drawDesc.Rotation.Y * d2r) *
                        Matrix4x4.CreateRotationX(-drawDesc.Rotation.X * d2r) *
                        Matrix4x4.CreateTranslation(drawDesc.Draw) *
                        camera *
                        PerspectiveMatrix;

                    _renderEffect!.TransformMatrix = world;
                    _centeringEffect!.TransformMatrix = Matrix3x2.CreateTranslation(width / 2f, height / 2f);
                    _opacityEffect!.Matrix = new Matrix5x4
                    {
                        M11 = 1f,
                        M22 = 1f,
                        M33 = 1f,
                        M44 = (float)Math.Max(0.0, opacity),
                    };

                    try
                    {
                        SafeTransform3DHelper.Apply(dc, _cropEffect!, _renderOutput!);
                        using var finalImg = _opacityEffect.Output;
                        dc.DrawImage(finalImg);
                    }
                    catch (SharpGenException)
                    {
                        // 入力画像が空または無効な境界の場合はクリア済みビットマップをそのまま使用
                    }
                }

                dc.EndDraw();
            }
            finally
            {
                dc.Transform = oldTransform;
                dc.Target = oldTarget;
            }
        }

        private void RenderDisplacementMap(
            ID2D1DeviceContext6 dc,
            EffectDescription desc,
            int width,
            int height,
            bool stretchMapToFit,
            ID2D1Bitmap1 targetBitmap)
        {
            var currentBrushType = _item.Brush.Type;
            if (_brushSource == null || _lastBrushType != currentBrushType)
            {
                if (_brushSource != null)
                    disposer.RemoveAndDispose(ref _brushSource);
                _brushSource = _item.Brush.CreateBrush(_devices);
                disposer.Collect(_brushSource);
                _lastBrushType = currentBrushType;
            }

            var screenRect = new RawRectF(-width / 2f, -height / 2f, width / 2f, height / 2f);
            _brushSource.Update(new BrushSourceDescription(desc, screenRect));

            var brush = _brushSource.Brush;
            var worldToPixel = Matrix3x2.CreateTranslation(width / 2f, height / 2f);

            var oldTarget = dc.Target;
            var oldTransform = dc.Transform;
            try
            {
                dc.Target = targetBitmap;
                dc.BeginDraw();
                dc.Clear(new Color4(0f, 0f, 0f, 0f));

                if (stretchMapToFit && TryGetNaturalMapBounds(desc, currentBrushType, brush, out var mapRect))
                {
                    float mapW = mapRect.Right - mapRect.Left;
                    float mapH = mapRect.Bottom - mapRect.Top;
                    float mapCx = (mapRect.Left + mapRect.Right) * 0.5f;
                    float mapCy = (mapRect.Top + mapRect.Bottom) * 0.5f;

                    dc.Transform =
                        Matrix3x2.CreateTranslation(-mapCx, -mapCy) *
                        Matrix3x2.CreateScale(width / mapW, height / mapH) *
                        worldToPixel;
                    dc.FillRectangle(mapRect, brush);
                }
                else
                {
                    dc.Transform = worldToPixel;
                    dc.FillRectangle(screenRect, brush);
                }

                dc.EndDraw();
            }
            finally
            {
                dc.Transform = oldTransform;
                dc.Target = oldTarget;
            }
        }

        private bool TryGetNaturalMapBounds(
            EffectDescription desc,
            Type? brushType,
            ID2D1Brush brush,
            out RawRectF mapRect)
        {
            if (brush is ID2D1BitmapBrush bitmapBrush)
            {
                var bmp = bitmapBrush.Bitmap;
                if (bmp != null)
                {
                    var px = bmp.PixelSize;
                    if (px.Width > 0 && px.Height > 0)
                    {
                        mapRect = new RawRectF(-px.Width / 2f, -px.Height / 2f, px.Width / 2f, px.Height / 2f);
                        return true;
                    }
                }
            }
            else if (brushType?.Name == "SceneBrushPlugin")
            {
                Guid sceneId = GetSceneIdFromBrushParameter(_item.Brush.Parameter);
                var sceneInfo = desc.Scenes.FirstOrDefault(s => s.ID == sceneId);
                if (sceneInfo != null && sceneInfo.Width > 0 && sceneInfo.Height > 0)
                {
                    mapRect = new RawRectF(
                        -sceneInfo.Width / 2f,
                        -sceneInfo.Height / 2f,
                        sceneInfo.Width / 2f,
                        sceneInfo.Height / 2f);
                    return true;
                }
            }

            mapRect = default;
            return false;
        }

        private Guid GetSceneIdFromBrushParameter(IBrushParameter parameter)
        {
            var paramType = parameter.GetType();
            if (_cachedSceneParamType != paramType)
            {
                _cachedSceneParamType = paramType;
                _cachedSceneIdProp = paramType.GetProperty("SceneId", BindingFlags.Public | BindingFlags.Instance);
            }
            if (_cachedSceneIdProp?.GetValue(parameter) is Guid id)
                return id;
            return Guid.Empty;
        }

        private static Matrix4x4 ApplyCameraAdjustments(DrawDescription drawDesc)
        {
            Matrix4x4 camera = drawDesc.Camera;
            if (drawDesc.Billboard != BillboardMode.None && TryCreateBillboardCorrection(camera, drawDesc.Billboard, out var correction))
            {
                var d = drawDesc.Draw;
                var adjusted = Matrix4x4.CreateTranslation(-d) * correction * Matrix4x4.CreateTranslation(d) * camera;
                if (IsFinite(adjusted))
                    camera = adjusted;
            }
            if (drawDesc.PerspectiveDistance is float dist && float.IsFinite(dist) && dist > 0f)
            {
                float m34 = 1f / 1000f - 1f / dist;
                if (float.IsFinite(m34))
                {
                    var adjusted = camera * (Matrix4x4.Identity with { M34 = m34 });
                    if (IsFinite(adjusted))
                        camera = adjusted;
                }
            }
            return camera;
        }

        private static bool TryCreateBillboardCorrection(Matrix4x4 camera, BillboardMode mode, out Matrix4x4 correction)
        {
            correction = Matrix4x4.Identity;
            if (!IsFinite(camera))
                return false;

            var rotOnly = new Matrix4x4(
                camera.M11, camera.M12, camera.M13, 0f,
                camera.M21, camera.M22, camera.M23, 0f,
                camera.M31, camera.M32, camera.M33, 0f,
                0f, 0f, 0f, 1f);

            float det = rotOnly.GetDeterminant();
            if (!float.IsFinite(det) || det <= 1e-6f)
                return false;

            if (!Matrix4x4.Decompose(rotOnly, out var scale, out var rotation, out _))
                return false;

            if (MathF.Abs(scale.X) <= 1e-6f || MathF.Abs(scale.Y) <= 1e-6f || MathF.Abs(scale.Z) <= 1e-6f ||
                rotation.LengthSquared() <= 1e-6f)
                return false;

            var q = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation));
            if (mode == BillboardMode.Spherical)
            {
                correction = Matrix4x4.Transpose(q);
                return IsFinite(correction);
            }
            if (mode == BillboardMode.Cylindrical)
            {
                var fwd = new Vector2(q.M31, q.M33);
                if (fwd.LengthSquared() > 1e-6f)
                {
                    fwd = Vector2.Normalize(fwd);
                    correction = Matrix4x4.CreateRotationY(-MathF.Atan2(fwd.X, fwd.Y));
                }
                return IsFinite(correction);
            }
            return false;
        }

        private static bool IsFinite(Matrix4x4 m)
        {
            return float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14) &&
                   float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24) &&
                   float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34) &&
                   float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);
        }
    }
}
