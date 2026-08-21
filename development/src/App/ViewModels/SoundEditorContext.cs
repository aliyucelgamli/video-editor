using VideoEditor.App.Services;
using VideoEditor.Domain;
using VideoEditor.Domain.Effects;
using VideoEditor.MediaEngine;
using VideoEditor.MediaEngine.Ffmpeg;

namespace VideoEditor.App.ViewModels;

/// <summary>
/// The services the sound editor borrows from the running app, bundled so the
/// window keeps a two-argument constructor. Built once by
/// <c>MainViewModel.BuildSoundEditorContext</c>; the sound editor never reaches
/// into the project model except through <see cref="ResolveMedia"/>.
/// </summary>
public sealed record SoundEditorContext(
    FFmpegLocator Ffmpeg,
    CachePaths Cache,
    IEffectCatalog Catalog,
    TimelineVisualsService Visuals,
    MediaProbe Probe,
    Func<Guid, MediaItem?> ResolveMedia,
    string DefaultExportFolder);
