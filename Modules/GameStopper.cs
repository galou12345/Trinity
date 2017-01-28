using Trinity.Components.Adventurer.Game.Actors;
using Trinity.Framework.Objects;
using Zeta.Bot;

namespace Trinity.Modules
{
    /// <summary>
    /// Stops the bot when certain conditions are met
    /// </summary>
    public class GameStopper : Module
    {
        protected override int UpdateIntervalMs => 1000;

        protected override void OnPulse()
        {
            if (!BotMain.IsRunning)
                return;

            //if(ActorFinder.FindNearestDeathGate() != null)
            //    BotMain.Stop();

            //if (Core.Settings.Advanced.StopOnGoblins)
            //{
            //    var goblin = Core.Actors.AllRActors.FirstOrDefault(g => g.IsTreasureGoblin);
            //    if (goblin != null)
            //    {
            //        Logger.Warn($"Stopping Bot because: Goblin Found! {goblin}");
            //        BotMain.Stop();
            //    }
            //}
        }

    }
}
