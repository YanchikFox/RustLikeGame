
namespace FSM
{
    public class NotInBuilding : BuildingState
    {
        public NotInBuilding(StateMachine stateMachine) : base("notInBuilding", stateMachine) { }

        public override void UpdateLogic()
        {
            base.UpdateLogic();
            if(sm.constructionSelector.selectedObject != null)
                stateMachine.ChangeState(sm.startState);
        }
    }
}