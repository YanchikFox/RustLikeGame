using UnityEngine;

namespace FSM
{
    public class StateMachine : MonoBehaviour
    {
        BaseState currentState;
        protected Vector2 guiOffset = Vector2.zero;
        void Start()
        {
            currentState = GetInitialState();
            if (currentState != null)
                currentState.Enter();
        }

        void Update()
        {
            if (currentState != null)
                currentState.UpdateLogic();
        }

        void LateUpdate()
        {
            if (currentState != null)
                currentState.UpdatePhysics();
        }

        protected virtual BaseState GetInitialState()
        {
            return null;
        }

        public void ChangeState(BaseState newState)
        {
            currentState.Exit();

            currentState = newState;
            newState.Enter();
        }

        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(10f + guiOffset.x, 10f + guiOffset.y, 200f, 100f));
            string content = currentState != null ? currentState.name : "(no current state)";
            GUILayout.Label($"<color='black'><size=40>{content}</size></color>");
            GUILayout.EndArea();
        }
    }
}