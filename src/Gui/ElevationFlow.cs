namespace RamOptimiser.Gui
{
    /// <summary>Idle, waiting on the UAC prompt, or on the way out (after a launch, or the user's exit).</summary>
    public enum ElevationState { Idle, Prompting, Exiting }

    /// <summary>What happened: a click on Restart as administrator, the prompt's outcome, or an exit.</summary>
    public enum ElevationEvent { Requested, Launched, Cancelled, Failed, ExitRequested }

    /// <summary>What the window does about it. Ignore and None both change nothing on screen.</summary>
    public enum ElevationAction { None, StartPrompt, ExitApp, ShowError, Ignore }

    public sealed class ElevationStep
    {
        public readonly ElevationState State;
        public readonly ElevationAction Action;

        public ElevationStep(ElevationState state, ElevationAction action)
        {
            State = state;
            Action = action;
        }
    }

    /// <summary>
    /// The Restart as administrator handoff (P10). The prompt runs off the UI thread, so a second click while it
    /// is up is ignored, and a result that arrives after the user already left changes nothing. The copy keeps
    /// its single-instance lock throughout and only gives it up by exiting after a successful launch.
    /// </summary>
    public static class ElevationFlow
    {
        public static ElevationStep Next(ElevationState state, ElevationEvent e)
        {
            if (state == ElevationState.Exiting) return Step(ElevationState.Exiting, ElevationAction.Ignore);
            // Exit already runs: nothing to show, and a later prompt result is ignored.
            if (e == ElevationEvent.ExitRequested) return Step(ElevationState.Exiting, ElevationAction.None);
            if (state == ElevationState.Idle)
                return e == ElevationEvent.Requested
                    ? Step(ElevationState.Prompting, ElevationAction.StartPrompt)
                    : Step(ElevationState.Idle, ElevationAction.Ignore);
            switch (e)
            {
                case ElevationEvent.Launched: return Step(ElevationState.Exiting, ElevationAction.ExitApp);
                case ElevationEvent.Cancelled: return Step(ElevationState.Idle, ElevationAction.None);
                case ElevationEvent.Failed: return Step(ElevationState.Idle, ElevationAction.ShowError);
                default: return Step(ElevationState.Prompting, ElevationAction.Ignore);
            }
        }

        static ElevationStep Step(ElevationState state, ElevationAction action)
        {
            return new ElevationStep(state, action);
        }
    }
}
