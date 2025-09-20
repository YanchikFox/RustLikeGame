
namespace FSM
{
    public class BuildingState : BaseState
    {
        protected BuildSM sm;

        public BuildingState(string name, StateMachine stateMachine) : base(name, stateMachine)
        {
            sm = (BuildSM)this.stateMachine;
        }
    }
}