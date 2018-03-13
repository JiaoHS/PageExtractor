using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PageExtractor
{
    internal class MapReduce<T>
    {
        private readonly Dictionary<T, double> _map;

        public MapReduce()
        {
            _map = new Dictionary<T, double>();
        }

        public double Reduce(T key, double value)
        {
            if (_map.ContainsKey(key))
            {
                _map[key] += value;
                return _map[key];
            }
            _map.Add(key, value);
            return value;
        }

        public double GetOrSetDefaultValue(T key)
        {
            if (_map.ContainsKey(key))
                return _map.Values.ToList()[0];
            //_map.Values.ToList()[0];
            //_map[key];
            _map.Add(key, 0.0);
            return 0.0;
        }
    }
}
