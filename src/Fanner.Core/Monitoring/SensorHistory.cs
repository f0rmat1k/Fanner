using Fanner.Core.Model;

namespace Fanner.Core.Monitoring;

/// <summary>
/// Fixed-length rolling history per channel, for the sparklines and charts.
/// </summary>
/// <remarks>
/// Written by the monitor's polling thread and read by the UI thread, so every
/// member takes the lock. Readers get a copy: a chart that is halfway through
/// drawing must not see the buffer wrap underneath it.
/// </remarks>
public sealed class SensorHistory(int capacity = 600)
{
    private readonly Dictionary<string, Series> _series = [];
    private readonly Lock _gate = new();

    /// <summary>Samples kept per channel. At one sample per second, 600 is ten minutes.</summary>
    public int Capacity { get; } = capacity > 0
        ? capacity
        : throw new ArgumentOutOfRangeException(nameof(capacity));

    public void Record(HardwareSnapshot snapshot)
    {
        lock (_gate)
        {
            foreach (var sensor in snapshot.AllSensors)
            {
                Append(sensor.Id, sensor.Value);
            }

            foreach (var fan in snapshot.Fans)
            {
                Append(RpmSeriesId(fan.Id), fan.Rpm);
                Append(DutySeriesId(fan.Id), fan.DutyPercent);
            }
        }
    }

    /// <summary>
    /// A copy of the samples for one channel, oldest first. Gaps — polls where the
    /// channel had no reading — come back as <see cref="float.NaN"/> so the chart
    /// can break the line instead of drawing through a hole.
    /// </summary>
    public float[] Read(string seriesId)
    {
        lock (_gate)
        {
            return _series.TryGetValue(seriesId, out var series)
                ? series.ToArray()
                : [];
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _series.Clear();
        }
    }

    /// <summary>History key for a fan header's tachometer.</summary>
    public static string RpmSeriesId(string fanId) => $"{fanId}#rpm";

    /// <summary>History key for a fan header's control output.</summary>
    public static string DutySeriesId(string fanId) => $"{fanId}#duty";

    private void Append(string id, double? value)
    {
        if (!_series.TryGetValue(id, out var series))
        {
            series = new Series(Capacity);
            _series[id] = series;
        }

        series.Add(value is null ? float.NaN : (float)value.Value);
    }

    /// <summary>Circular buffer that overwrites its oldest sample once full.</summary>
    private sealed class Series(int capacity)
    {
        private readonly float[] _buffer = new float[capacity];
        private int _start;
        private int _count;

        public void Add(float value)
        {
            if (_count < _buffer.Length)
            {
                _buffer[(_start + _count) % _buffer.Length] = value;
                _count++;
            }
            else
            {
                _buffer[_start] = value;
                _start = (_start + 1) % _buffer.Length;
            }
        }

        public float[] ToArray()
        {
            var result = new float[_count];
            for (var i = 0; i < _count; i++)
            {
                result[i] = _buffer[(_start + i) % _buffer.Length];
            }

            return result;
        }
    }
}
