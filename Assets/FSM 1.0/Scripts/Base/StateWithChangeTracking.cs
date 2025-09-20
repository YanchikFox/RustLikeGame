namespace FSM
{
    /// <summary>
    /// ������� ����� ��� ��������� � ������������� ���������
    /// ��������� ������������ ������ hasStateChanged
    /// </summary>
    public abstract class StateWithChangeTracking : BaseState
    {
        protected bool hasStateChanged = false;
        
        public StateWithChangeTracking(string name, StateMachine stateMachine) : base(name, stateMachine)
        {
        }
        
        public override void Enter()
        {
            base.Enter();
            hasStateChanged = false;
        }
        
        /// <summary>
        /// ���������� ������� � ������ ��������� � ��������� ���������
        /// </summary>
        protected bool TryChangeState(BaseState newState)
        {
            if (hasStateChanged) return false;
            
            stateMachine.ChangeState(newState);
            hasStateChanged = true;
            return true;
        }
        
        /// <summary>
        /// �������� ������� ��� ����� ���������
        /// ������ ���� ����������� � �����������
        /// </summary>
        protected abstract void CheckTransitionConditions();
        
        public override void UpdateLogic()
        {
            base.UpdateLogic();
            if (!hasStateChanged)
            {
                CheckTransitionConditions();
            }
        }
    }
}

