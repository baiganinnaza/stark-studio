using System;
using System.Collections.Generic;
using Stark.Core.Json;
using Stark.Core.Steps;

namespace Stark.Core.Session
{
    /// <summary>
    /// Журнал сессии для отчёта пилота: время на каждом шаге, возвраты назад, события.
    /// Время берётся из переданных часов (в Unity: Time.realtimeSinceStartupAsDouble),
    /// поэтому журнал тестируется без Unity.
    /// </summary>
    public sealed class SessionLog
    {
        readonly Func<double> _clock;
        readonly string _productId;
        readonly string _startedAtUtc;
        readonly double _t0;
        readonly List<Dictionary<string, object>> _events = new List<Dictionary<string, object>>();
        readonly Dictionary<int, double> _secondsPerStep = new Dictionary<int, double>();
        readonly Dictionary<int, int> _visitsPerStep = new Dictionary<int, int>();
        int _currentStep = -1;
        double _stepEnteredAt;
        int _backCount;
        bool _finished;

        public SessionLog(string productId, Func<double> clock, DateTime? startedAtUtc = null)
        {
            _productId = productId ?? "";
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _t0 = clock();
            _startedAtUtc = (startedAtUtc ?? DateTime.UtcNow).ToString("yyyy-MM-ddTHH:mm:ssZ");
        }

        public int BackCount => _backCount;
        public double Elapsed => _clock() - _t0;

        public double SecondsOnStep(int step)
        {
            double s = _secondsPerStep.TryGetValue(step, out var v) ? v : 0;
            if (step == _currentStep && !_finished) s += _clock() - _stepEnteredAt;
            return s;
        }

        public void EnterStep(int step, StepChangeReason reason)
        {
            CloseCurrent();
            if (reason == StepChangeReason.Prev) _backCount++;
            _currentStep = step;
            _stepEnteredAt = _clock();
            _visitsPerStep[step] = (_visitsPerStep.TryGetValue(step, out var n) ? n : 0) + 1;
            Add("step", new Dictionary<string, object> { { "step", step }, { "reason", reason.ToString() } });
        }

        /// <summary>Произвольное событие: placed, explode, scale, help, error...</summary>
        public void Mark(string kind, string detail = null)
        {
            var data = new Dictionary<string, object>();
            if (detail != null) data["detail"] = detail;
            Add(kind, data);
        }

        public void Finish()
        {
            if (_finished) return;
            CloseCurrent();
            _finished = true;
            Add("finished", new Dictionary<string, object>());
        }

        void CloseCurrent()
        {
            if (_currentStep < 0 || _finished) return;
            double dt = _clock() - _stepEnteredAt;
            _secondsPerStep[_currentStep] = (_secondsPerStep.TryGetValue(_currentStep, out var v) ? v : 0) + dt;
        }

        void Add(string kind, Dictionary<string, object> data)
        {
            data["t"] = Math.Round(_clock() - _t0, 3);
            data["kind"] = kind;
            _events.Add(data);
        }

        public string ToJson()
        {
            var steps = new List<object>();
            var keys = new List<int>(_visitsPerStep.Keys);
            keys.Sort();
            foreach (var k in keys)
                steps.Add(new Dictionary<string, object>
                {
                    { "step", k },
                    { "seconds", Math.Round(SecondsOnStep(k), 2) },
                    { "visits", _visitsPerStep[k] },
                });
            var root = new Dictionary<string, object>
            {
                { "format", "stark-session-1" },
                { "productId", _productId },
                { "startedAt", _startedAtUtc },
                { "totalSeconds", Math.Round(Elapsed, 2) },
                { "finished", _finished },
                { "backCount", _backCount },
                { "steps", steps },
                { "events", _events },
            };
            return MiniJson.Serialize(root);
        }
    }
}
