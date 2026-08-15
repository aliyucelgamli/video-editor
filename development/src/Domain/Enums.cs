namespace VideoEditor.Domain;

public enum MediaType { Video, Audio, Image }

public enum TrackType { Video, Audio, Overlay }

public enum EasingType
{
    Linear,
    InSine,
    OutSine,
    InOutSine,
    InQuad,
    OutQuad,
    InOutQuad,
    InCubic,
    OutCubic,
    InOutCubic,
    InBack,
    OutBack,
    InOutBack
}

public enum KeyframeInterpolation { Linear, EaseIn, EaseOut, EaseInOut, Hold }
