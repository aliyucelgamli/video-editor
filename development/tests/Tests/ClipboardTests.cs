using VideoEditor.Domain;

namespace VideoEditor.Tests;

/// <summary>
/// Copy/paste and duplicate rest on one thing: a clone that shares nothing
/// mutable with the original. If it did, editing the copy would silently edit
/// the clip it came from.
/// </summary>
public static class ClipboardTests
{
    public static void Register()
    {
        TestRunner.Add("Clone: copies every edit but takes a new identity", () =>
        {
            var original = new TimelineEvent
            {
                Name = "Shot 1", MediaId = Guid.NewGuid(),
                Start = 4, Duration = 3, SourceIn = 1.5, SourceOut = 4.5,
                PlaybackRate = 1.25, FadeInDuration = 0.4, FadeOutDuration = 0.6,
                FadeInEasing = EasingType.OutBack, FadeOutEasing = EasingType.InOutSine,
                Volume = 1.4, Opacity = 0.8, Muted = true, Layer = 2,
                LinkedEventId = Guid.NewGuid()
            };
            original.Transform.ScaleX = 1.7;
            original.Transform.PositionY = -40;
            original.Effects.Add(new EffectInstance
            {
                Type = "brightness", Enabled = false,
                Parameters = { ["amount"] = 0.35 }
            });
            original.Keyframes.Add(new KeyframeTrack
            {
                Property = "opacity",
                Keyframes = { new Keyframe { Time = 0.5, Value = 0.25 } }
            });

            var copy = original.Clone();

            Assert.True(copy.Id != original.Id, "the copy is its own clip");
            Assert.Equal(original.Name, copy.Name, "name copied");
            Assert.Equal(original.MediaId, copy.MediaId, "same source media");
            Assert.Close(original.SourceIn, copy.SourceIn, "trim in copied", 0.0001);
            Assert.Close(original.SourceOut, copy.SourceOut, "trim out copied", 0.0001);
            Assert.Close(original.PlaybackRate, copy.PlaybackRate, "speed copied", 0.0001);
            Assert.Close(original.FadeInDuration, copy.FadeInDuration, "fade in copied", 0.0001);
            Assert.Equal(original.FadeInEasing, copy.FadeInEasing, "fade easing copied");
            Assert.Close(original.Volume, copy.Volume, "volume copied", 0.0001);
            Assert.Close(original.Opacity, copy.Opacity, "opacity copied", 0.0001);
            Assert.Equal(original.Layer, copy.Layer, "layer copied");
            Assert.True(copy.Muted, "mute copied");
            Assert.Close(1.7, copy.Transform.ScaleX, "transform copied", 0.0001);

            // A pasted clip must not claim the original's partner.
            Assert.True(copy.LinkedEventId is null, "the link is dropped, the caller re-pairs");

            // Deep, not shallow: touching the copy must never reach the original.
            copy.Transform.ScaleX = 3;
            Assert.Close(1.7, original.Transform.ScaleX, "transform is not shared", 0.0001);

            copy.Effects[0].Parameters["amount"] = 0.9;
            copy.Effects[0].Enabled = true;
            Assert.Close(0.35, original.Effects[0].Parameters["amount"], "effect args not shared", 0.0001);
            Assert.False(original.Effects[0].Enabled, "effect state not shared");
            Assert.True(copy.Effects[0].Id != original.Effects[0].Id, "effects get new identities");

            copy.Keyframes[0].Keyframes[0].Value = 1;
            Assert.Close(0.25, original.Keyframes[0].Keyframes[0].Value, "keyframes not shared", 0.0001);

            copy.Effects.Add(new EffectInstance { Type = "blur" });
            Assert.Equal(1, original.Effects.Count, "the effect list itself is not shared");
        });

        TestRunner.Add("Clone: a title keeps its style, independently", () =>
        {
            var title = new TimelineEvent
            {
                Name = "Title", Duration = 3,
                Text = new TextStyle { Content = "Hello", FontSize = 72, Bold = false }
            };

            var copy = title.Clone();
            Assert.Equal("Hello", copy.Text!.Content, "text copied");
            Assert.Close(72, copy.Text.FontSize, "font size copied", 0.0001);

            copy.Text.Content = "Changed";
            Assert.Equal("Hello", title.Text!.Content, "the style is not shared");
        });
    }
}
