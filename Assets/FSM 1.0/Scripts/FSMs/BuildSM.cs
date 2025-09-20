using UnityEngine;
namespace FSM
{
    public class BuildSM : StateMachine
    {
        [HideInInspector] public Start startState;
        [HideInInspector] public Stop stopState;
        [HideInInspector] public Perform performState;
        [HideInInspector] public NotInBuilding notInBuildState;
        [HideInInspector] public Placer placementMachine;
        [HideInInspector] public ConstructionSelector constructionSelector;
        private void Awake()
        {
            constructionSelector = GetComponent<ConstructionSelector>();
            placementMachine = GetComponent<Placer>();
            guiOffset = new Vector2(0f, 240f);
            startState = new Start(this);
            stopState = new Stop(this);
            performState = new Perform(this);
            notInBuildState = new NotInBuilding(this);
        }

        protected override BaseState GetInitialState()
        {
            return notInBuildState;
        }
    }
}