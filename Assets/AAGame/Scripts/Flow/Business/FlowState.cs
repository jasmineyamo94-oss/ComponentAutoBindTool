using System;

public enum FlowStage
{
    Bootstrap,
    Start,
    Main,
    Level,
}

public sealed class FlowState
{
    public FlowStage Stage;
    public string Error = "";
    public float MainSeconds,
        LevelSeconds;
    public int Restarts;
}

public sealed class LevelLoadRequestContent
{
    public int LevelId { get; }

    public LevelLoadRequestContent(int levelId)
    {
        if (levelId <= 0)
            throw new ArgumentOutOfRangeException(nameof(levelId));
        LevelId = levelId;
    }
}
