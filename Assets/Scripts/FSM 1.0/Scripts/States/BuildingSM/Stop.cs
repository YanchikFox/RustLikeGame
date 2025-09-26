namespace FSM
{
    public class Stop : BuildingState
    {
        public Stop(StateMachine stateMachine) : base("Stop", stateMachine){ }
        public override void Enter()
        {
            base.Enter();
            sm.placementMachine.StopPlacement();
            stateMachine.ChangeState(sm.notInBuildState);
            sm.constructionSelector.selectedObject = null;
        }
    }
}