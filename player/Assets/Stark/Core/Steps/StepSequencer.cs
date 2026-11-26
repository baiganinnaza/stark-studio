using System;
using System.Collections.Generic;
using Stark.Core.Package;

namespace Stark.Core.Steps
{
    /// <summary>Как показывать деталь на текущем шаге.</summary>
    public enum PartState
    {
        /// <summary>Установлена на прошлых шагах: родные цвета CAD.</summary>
        Done,
        /// <summary>Основная деталь текущего шага: голубая, пульсирует, «прилетает» по пути.</summary>
        Current,
        /// <summary>Крепёж текущего шага: оранжевый.</summary>
        CurrentFastener,
        /// <summary>Будет на следующих шагах: скрыта или «призрак».</summary>
        Future,
    }

    /// <summary>
    /// Конечный автомат шагов: Next / Prev / GoTo. Индекс -1 не используется:
    /// после загрузки сразу шаг 0. После последнего шага есть состояние «готово» (IsFinished).
    /// </summary>
    public sealed class StepSequencer
    {
        readonly StarkPackage _pkg;
        readonly Dictionary<string, int> _stepOfPart = new Dictionary<string, int>();
        readonly HashSet<string> _fasteners = new HashSet<string>();

        public event Action<StepChange> StepChanged;

        public StepSequencer(StarkPackage pkg)
        {
            _pkg = pkg ?? throw new ArgumentNullException(nameof(pkg));
            foreach (var step in pkg.Steps)
            {
                foreach (var id in step.Parts) _stepOfPart[id] = step.Index;
                foreach (var f in step.Fasteners)
                foreach (var id in f.PartIds)
                {
                    _stepOfPart[id] = step.Index;
                    _fasteners.Add(id);
                }
            }
            CurrentIndex = 0;
        }

        public int Count => _pkg.Steps.Count;
        /// <summary>0..Count-1, либо Count, когда сборка завершена.</summary>
        public int CurrentIndex { get; private set; }
        public bool IsFinished => CurrentIndex >= Count;
        public AssemblyStep Current => IsFinished ? null : _pkg.Steps[CurrentIndex];
        public bool CanGoNext => !IsFinished;
        public bool CanGoPrev => CurrentIndex > 0;

        /// <summary>Номер шага, на котором ставится деталь, или -1 (деталь без шага: показываем как готовую).</summary>
        public int StepOfPart(string partId) => _stepOfPart.TryGetValue(partId, out var i) ? i : -1;

        public PartState StateOf(string partId)
        {
            int s = StepOfPart(partId);
            if (s < 0 || s < CurrentIndex) return PartState.Done;
            if (s > CurrentIndex) return PartState.Future;
            return _fasteners.Contains(partId) ? PartState.CurrentFastener : PartState.Current;
        }

        public bool Next() => GoTo(CurrentIndex + 1, StepChangeReason.Next);
        public bool Prev() => GoTo(CurrentIndex - 1, StepChangeReason.Prev);

        /// <summary>Перейти к шагу 0..Count (Count = «сборка завершена»).</summary>
        public bool GoTo(int index, StepChangeReason reason = StepChangeReason.Jump)
        {
            if (index < 0 || index > Count || index == CurrentIndex) return false;
            int from = CurrentIndex;
            CurrentIndex = index;
            StepChanged?.Invoke(new StepChange(from, index, reason));
            return true;
        }

        public void Restart() => GoTo(0, StepChangeReason.Jump);

        /// <summary>Прогресс 0..1 по числу пройденных шагов.</summary>
        public float Progress => Count == 0 ? 1f : (float)Math.Min(CurrentIndex, Count) / Count;
    }

    public enum StepChangeReason { Next, Prev, Jump }

    public readonly struct StepChange
    {
        public readonly int From;
        public readonly int To;
        public readonly StepChangeReason Reason;
        public StepChange(int from, int to, StepChangeReason reason) { From = from; To = to; Reason = reason; }
    }
}
