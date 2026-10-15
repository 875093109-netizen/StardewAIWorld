namespace AIWorld.Core.Behaviors;

public static class PennyBranchObserver
{
    public static StoryOutcome Observe(bool skipped, bool forked, bool matchesMain, bool matchesRejection,
        int command, int forkCommand, int commandCount)
    {
        if (skipped) return StoryOutcome.Unknown;
        if (forked && matchesRejection) return StoryOutcome.Rejected;
        if (!forked && matchesMain && forkCommand >= 0 && command > forkCommand && command < commandCount)
            return StoryOutcome.Accepted;
        return StoryOutcome.Unknown;
    }
}
