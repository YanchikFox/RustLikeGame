using UnityEngine;
namespace FSM
{
    public class Perform : BuildingState
    {
        public Perform(StateMachine stateMachine) : base("Perform", stateMachine){ }

        public override void UpdateLogic()
        {
            base.UpdateLogic();
            sm.placementMachine.PerformPlacement();
            if(Input.GetMouseButtonDown(0))
                stateMachine.ChangeState(sm.stopState);
        }
        
    }
}