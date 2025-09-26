namespace FSM
{
    public class Start : BuildingState
    {
        public Start(StateMachine stateMachine) : base("Start", stateMachine) { }
        

        public override void UpdateLogic()
        {
            base.UpdateLogic();
            sm.placementMachine.StartPlacement();
            stateMachine.ChangeState(sm.performState);
        }
    }
}