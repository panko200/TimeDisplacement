using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.Brush;
using YukkuriMovieMaker.Plugin.Effects;
using YukkuriMovieMaker.Project;
using Brush = YukkuriMovieMaker.Plugin.Brush.Brush;

namespace TimeDisplacement
{
    [VideoEffect("タイムディスプレイスメント", ["加工"], ["time", "displacement", "タイムディスプレイスメント", "時間", "ずれ", "ずらし", "スリットスキャン"], true, true)]
    public class TimeDisplacementEffect : VideoEffectBase
    {
        public override string Label => "タイムディスプレイスメント";

        [Display(GroupName = "タイムディスプレイスメント", Name = "最大変位時間", Description = "最大で何秒まで時間をずらすかを指定します。")]
        [AnimationSlider("F2", "秒", -5.0, 5.0)]
        public Animation MaxDisplacementTime { get; } = new Animation(1.0, -60.0, 60.0);

        [Display(GroupName = "タイムディスプレイスメント", Name = "時間解像度", Description = "1秒あたりに時間を分割するコマ数(fps)を指定します。\n値を下げると階段状に時間がずれます。")]
        [AnimationSlider("F1", "fps", 1.0, 60.0)]
        public Animation TimeResolution { get; } = new Animation(30.0, 0.1, 300.0);

        [Display(GroupName = "タイムディスプレイスメント", Name = "マップをサイズに合わせる", Description = "ONにすると、画像やシーンなどの変位マップを画面サイズに合わせて引き伸ばします。")]
        [ToggleSlider]
        public bool StretchMapToFit
        {
            get => stretchMapToFit;
            set => Set(ref stretchMapToFit, value);
        }
        private bool stretchMapToFit = true;

        [Display(GroupName = "タイムディスプレイスメント", Name = "時間シフト基準", Description = "変位マップの輝度に対する時間シフトの方向と基準を選択します。")]
        [EnumComboBox]
        public TimeShiftMode TimeShiftMode
        {
            get => timeShiftMode;
            set => Set(ref timeShiftMode, value);
        }
        private TimeShiftMode timeShiftMode = TimeShiftMode.PastFromBlack;

        [Display(GroupName = "タイムディスプレイスメント", Name = "フレーム補間", Description = "ONにすると、フレーム間の時間位置を滑らかに補間します。")]
        [ToggleSlider]
        public bool EnableFrameBlending
        {
            get => enableFrameBlending;
            set => Set(ref enableFrameBlending, value);
        }
        private bool enableFrameBlending = false;

        [Display(GroupName = "タイムディスプレイスメント", Name = "マップを表示", Description = "ONにすると、変位マップの輝度プレビューを表示します。")]
        [ToggleSlider]
        public bool IsMapPreviewEnabled
        {
            get => isMapPreviewEnabled;
            set => Set(ref isMapPreviewEnabled, value);
        }
        private bool isMapPreviewEnabled = false;

        [Display(GroupName = "変位マップ", AutoGenerateField = true)]
        public Brush Brush { get; } = CreateDefaultBrush();

        private static Brush CreateDefaultBrush()
        {
            if (PluginLoader.Plugins == null)
                return new Brush();

            var pluginType = PluginLoader.BrushPlugins
                .FirstOrDefault(p => p.GetType().Name == "LinearGradientBrushPlugin")?.GetType()
                ?? PluginLoader.BrushPlugins.FirstOrDefault(p => p.GetType().Name == "BitmapBrushPlugin")?.GetType()
                ?? PluginLoader.BrushPlugins.FirstOrDefault()?.GetType();

            return pluginType != null ? new Brush(pluginType) : new Brush();
        }

        public override IEnumerable<string> CreateExoVideoFilters(int keyFrameIndex, ExoOutputDescription exoOutputDescription)
        {
            return [];
        }

        public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices)
        {
            return new TimeDisplacementEffectProcessor(devices, this);
        }

        protected override IEnumerable<IAnimatable> GetAnimatables()
        {
            yield return MaxDisplacementTime;
            yield return TimeResolution;
            yield return Brush;
        }

        public override IEnumerable<string> GetFiles()
        {
            foreach (var file in base.GetFiles())
                yield return file;
            foreach (var file in Brush.GetFiles())
                yield return file;
        }

        public override void ReplaceFile(string from, string to)
        {
            base.ReplaceFile(from, to);
            Brush.ReplaceFile(from, to);
        }

        public override IEnumerable<TimelineResource> GetResources()
        {
            foreach (var resource in base.GetResources())
                yield return resource;
            foreach (var resource in Brush.GetResources())
                yield return resource;
        }
    }
}
