namespace FSM
{
    /// <summary>
    /// Ѕазовый класс дл€ состо€ний с отслеживанием изменений
    /// ”стран€ет дублирование логики hasStateChanged
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
        /// Ѕезопасный переход к новому состо€нию с проверкой изменений
        /// </summary>
        protected bool TryChangeState(BaseState newState)
        {
            if (hasStateChanged) return false;
            
            stateMachine.ChangeState(newState);
            hasStateChanged = true;
            return true;
        }
        
        /// <summary>
        /// ѕроверка условий дл€ смены состо€ни€
        /// ƒолжна быть реализована в наследниках
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

