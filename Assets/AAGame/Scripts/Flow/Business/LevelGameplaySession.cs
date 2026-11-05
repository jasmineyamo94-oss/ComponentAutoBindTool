public sealed class LevelGameplaySession
{
    public LevelLoadRequestContent Request { get; }
    public float Seconds { get; private set; }
    public int Restarts { get; private set; }

    public LevelGameplaySession(LevelLoadRequestContent request) => Request = request;

    public void Tick(float delta) => Seconds += delta;

    public void Restart()
    {
        Seconds = 0;
        Restarts++;
    }
}
