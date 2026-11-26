using System.Collections.Generic;
using Stark.Device;
using UnityEngine.InputSystem;

namespace Stark.Adapters.Keyboard
{
    /// <summary>
    /// Клавиатура и Bluetooth-презентер (кликер). Кликеры шлют стрелки / PageUp / PageDown,
    /// поэтому на Beam Pro это дешёвый запасной ввод «без рук» уже сейчас (≈2–5 тыс. тенге).
    /// В редакторе те же клавиши дублируют жесты.
    /// </summary>
    public sealed class KeyboardSystemInput : ISystemInput
    {
        /// <summary>Не трогать Q/E/R и т.д., пока пользователь крутит камеру симулятора (ПКМ).</summary>
        public System.Func<bool> SuppressLetters;

        public void Poll(List<SystemCommand> into)
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null) return;

            if (Down(kb.rightArrowKey) || Down(kb.pageDownKey) || Down(kb.downArrowKey)) into.Add(SystemCommand.NextStep);
            if (Down(kb.leftArrowKey) || Down(kb.pageUpKey) || Down(kb.upArrowKey)) into.Add(SystemCommand.PrevStep);
            if (Down(kb.enterKey) || Down(kb.numpadEnterKey) || Down(kb.spaceKey)) into.Add(SystemCommand.Confirm);
            if (Down(kb.equalsKey) || Down(kb.numpadPlusKey)) into.Add(SystemCommand.ScaleUp);
            if (Down(kb.minusKey) || Down(kb.numpadMinusKey)) into.Add(SystemCommand.ScaleDown);
            if (Down(kb.digit0Key) || Down(kb.numpad0Key)) into.Add(SystemCommand.ResetScale);

            if (SuppressLetters != null && SuppressLetters()) return;
            if (Down(kb.nKey)) into.Add(SystemCommand.NextStep);
            if (Down(kb.bKey)) into.Add(SystemCommand.PrevStep);
            if (Down(kb.xKey)) into.Add(SystemCommand.ToggleExplode);
            if (Down(kb.rKey)) into.Add(SystemCommand.Replace);
            if (Down(kb.qKey)) into.Add(SystemCommand.RotateLeft);
            if (Down(kb.eKey)) into.Add(SystemCommand.RotateRight);
            if (Down(kb.gKey)) into.Add(SystemCommand.ToggleFutureGhosts);
            if (Down(kb.lKey)) into.Add(SystemCommand.NextLanguage);
            if (Down(kb.oKey)) into.Add(SystemCommand.NextPackage);
        }

        static bool Down(UnityEngine.InputSystem.Controls.KeyControl key) => key != null && key.wasPressedThisFrame;
    }
}
