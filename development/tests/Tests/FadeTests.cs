using VideoEditor.Application.Effects;
using VideoEditor.Domain;
using VideoEditor.MediaEngine.Effects;
using VideoEditor.MediaEngine.Export;
using VideoEditor.MediaEngine.Frames;

namespace VideoEditor.Tests;

/// <summary>Eased fades, automatic crossfades and their audio-side mapping.</summary>
public static class FadeTests
{
    public static void Register()
    {
        TestRunner.Add("Easing: endpoints are exact, shapes behave", () =>
        {
            foreach (EasingType type in Enum.GetValues<EasingType>())
            {
                Assert.Close(0, Easing.Evaluate(type, 0), $"{type} starts at 0", 1e-9);
                Assert.Close(1, Easing.Evaluate(type, 1), $"{type} ends at 1", 1e-9);
            }

            Assert.Close(0.5, Easing.Evaluate(EasingType.Linear, 0.5), "linear midpoint");
            Assert.Close(0.5, Easing.Evaluate(EasingType.InOutSine, 0.5), "sine in-out midpoint");
            Assert.True(Easing.Evaluate(EasingType.InQuad, 0.5) < 0.5, "ease-in lags early");
            Assert.True(Easing.Evaluate(EasingType.OutQuad, 0.5) > 0.5, "ease-out leads early");
            Assert.True(Easing.Evaluate(EasingType.OutBack, 0.5) > 1.0, "back overshoots");
            Assert.Close(0.7, Easing.Evaluate(EasingType.Linear, 0.7), "input clamp keeps linearity");
        });

        TestRunner.Add("Crossfade: same-track overlaps create implicit fades", () =>
        {
            var track = new Track { Type = TrackType.Video };
            var first = new TimelineEvent { Start = 0, Duration = 10, SourceOut = 10 };
            var second = new TimelineEvent { Start = 8, Duration = 6, SourceOut = 6 };
            track.Events.Add(first);
            track.Events.Add(second);

            var firstFades = Crossfade.ImplicitFades(track, first);
            var secondFades = Crossfade.ImplicitFades(track, second);
            Assert.Close(0, firstFades.FadeIn, "first clip has no implicit fade in");
            Assert.Close(2, firstFades.FadeOut, "first fades out across the overlap");
            Assert.Close(2, secondFades.FadeIn, "second fades in across the overlap");
            Assert.Close(0, secondFades.FadeOut, "second has no implicit fade out");

            // Midpoint of the overlap: both sides sit at half opacity.
            Assert.Close(0.5, FrameCompositor.EffectiveFadeFactor(track, first, 9), "outgoing at half");
            Assert.Close(0.5, FrameCompositor.EffectiveFadeFactor(track, second, 9), "incoming at half");
            Assert.Close(1.0, FrameCompositor.EffectiveFadeFactor(track, first, 4), "no fade before overlap");

            // Stacked clips starting together do not crossfade.
            var stackedTrack = new Track { Type = TrackType.Video };
            stackedTrack.Events.Add(new TimelineEvent { Start = 0, Duration = 5 });
            stackedTrack.Events.Add(new TimelineEvent { Start = 0, Duration = 8 });
            var stacked = Crossfade.ImplicitFades(stackedTrack, stackedTrack.Events[0]);
            Assert.Close(0, stacked.FadeIn, "equal starts: no implicit fade in");
        });

        TestRunner.Add("Fades: easing shapes the factor curve", () =>
        {
            var evt = new TimelineEvent
            {
                Start = 0, Duration = 10,
                FadeInDuration = 4, FadeInEasing = EasingType.InQuad
            };
            Assert.Close(0.25, FrameCompositor.FadeFactor(evt, 2), "quad ease-in at halfway", 1e-9);

            evt.FadeInEasing = EasingType.Linear;
            Assert.Close(0.5, FrameCompositor.FadeFactor(evt, 2), "linear at halfway", 1e-9);

            evt.FadeInEasing = EasingType.OutBack;
            Assert.Close(1.0, FrameCompositor.FadeFactor(evt, 2), "overshoot clamps to 1", 1e-9);
        });

        TestRunner.Add("Audio: easing maps to afade curves, crossfade reaches the mix", () =>
        {
            Assert.Equal("tri", AudioFilterGraphBuilder.AfadeCurveFor(EasingType.Linear), "linear curve");
            Assert.Equal("hsin", AudioFilterGraphBuilder.AfadeCurveFor(EasingType.InOutSine), "smooth curve");
            Assert.Equal("hsin", AudioFilterGraphBuilder.AfadeCurveFor(EasingType.OutBack), "back has no gain overshoot");
            Assert.Equal("qsin", AudioFilterGraphBuilder.AfadeCurveFor(EasingType.OutSine), "out sine curve");

            var project = new Project();
            var media = new MediaItem { Name = "a", FilePath = "a.wav", Type = MediaType.Audio };
            project.Media.Items.Add(media);
            var track = new Track { Type = TrackType.Audio };
            project.Tracks.Add(track);
            track.Events.Add(new TimelineEvent { MediaId = media.Id, Start = 0, Duration = 10, SourceOut = 10 });
            track.Events.Add(new TimelineEvent { MediaId = media.Id, Start = 8, Duration = 6, SourceOut = 6 });

            var arguments = string.Join(" ", AudioMixPlanner.BuildMixArguments(
                project, new EffectCatalog(), new TimeRange { Start = 0, End = 14 }, 48000, "out.wav"));
            Assert.True(arguments.Contains("afade=t=out") && arguments.Contains("afade=t=in"),
                "overlap produces both fade directions in the mix");
            Assert.True(arguments.Contains("curve=hsin"), "afade carries the easing curve");
        });
    }
}
