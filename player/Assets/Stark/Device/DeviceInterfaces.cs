using Stark.Core.Gestures;
using UnityEngine;

namespace Stark.Device
{
    /// <summary>
    /// Поиск стола и якорь модели. На очках: AR Foundation (плоскости + ARAnchor),
    /// в редакторе: виртуальный стол симулятора. Presentation знает только этот интерфейс.
    /// </summary>
    public interface IPlacementService
    {
        /// <summary>Готов ли трекинг (на очках: сессия AR в состоянии Tracking).</summary>
        bool IsReady { get; }

        /// <summary>Сколько горизонтальных поверхностей найдено (для подсказки «осмотрите стол»).</summary>
        int SurfaceCount { get; }

        /// <summary>
        /// Луч (обычно взгляд: центр камеры вперёд) → точка на горизонтальной поверхности.
        /// Поворот pose: Y вверх, Z от пользователя.
        /// </summary>
        bool TryGetSurfacePose(Ray ray, out Pose pose);

        /// <summary>Создать якорь в pose. Модель становится дочерней к нему и держится на столе.</summary>
        Transform CreateAnchor(Pose pose);

        void ReleaseAnchor(Transform anchor);

        /// <summary>Показывать ли найденные плоскости (только на этапе размещения).</summary>
        void SetSurfaceVisualization(bool visible);
    }

    /// <summary>
    /// Кадры рук в мировых координатах. Адаптер отвечает только за суставы,
    /// жесты распознаёт общий Stark.Core.Gestures.GestureRecognizer.
    /// </summary>
    public interface IHandInput
    {
        bool IsAvailable { get; }
        HandFrame GetFrame(Hand hand);
    }

    public enum SystemCommand
    {
        None,
        NextStep,
        PrevStep,
        ToggleExplode,
        Replace,
        RotateLeft,
        RotateRight,
        ScaleUp,
        ScaleDown,
        ResetScale,
        ToggleFutureGhosts,
        NextLanguage,
        NextPackage,
        Confirm,
    }

    /// <summary>
    /// Запасной ввод без жестов: клавиатура, Bluetooth-кликер/презентер (шлёт стрелки и PageUp/PageDown),
    /// позже тачпад Beam Pro и голос. Руки сборщика заняты деталями, поэтому шаги обязательно
    /// дублируются без жестов.
    /// </summary>
    public interface ISystemInput
    {
        /// <summary>Команды, пришедшие с прошлого вызова.</summary>
        void Poll(System.Collections.Generic.List<SystemCommand> into);
    }
}

namespace Stark.Device
{
    /// <summary>
    /// Необязательно: указатель вместо взгляда (мышь в симуляторе, позже луч от руки).
    /// Если сервис не реализует его, размещение идёт по центру взгляда.
    /// </summary>
    public interface IPointerRayProvider
    {
        bool TryGetPointerRay(out Ray ray);
    }

    /// <summary>Объект, который симулятор может «нажать» кликом мыши (поки-кнопки).</summary>
    public interface IPointerPressable
    {
        void PressFromPointer();
    }

    /// <summary>Набор сервисов устройства, который App передаёт в StarkPlayer.</summary>
    public sealed class DeviceServices
    {
        public string Name;
        public Camera Camera;
        public IPlacementService Placement;
        public IHandInput Hands;
        public ISystemInput[] SystemInputs;
    }
}
