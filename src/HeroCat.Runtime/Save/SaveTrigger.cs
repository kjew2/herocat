namespace HeroCat.Runtime.Save
{
    /// <summary>Why a save is written (02 section 8.1). Low memory saves as Pause and boot as Resume (01 sections 3.3, 8.1).</summary>
    public enum SaveTrigger { Periodic, Pause, Quit, Resume, Summon, Inbox, Dungeon, Debug, Conflict }
}
