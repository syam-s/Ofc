﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ConsoleApp1
{
    public class HuffmanCoding<T> where T : IEquatable<T>
    {
        private readonly IEnumerable<HuffmanLeafNode<T>> _occurenceValue;

        public HuffmanCoding(IDictionary<int, T> occurenceValueDictionary)
        {
            _occurenceValue = occurenceValueDictionary.Select(item => new HuffmanLeafNode<T>(item.Key, item.Value));
        }

        //public HuffmanCoding(ICollection<KeyValuePair<int, T>> occurenceValueCollection)
        //{
        //    _occurenceValue = occurenceValueCollection.ToDictionary(item => item.Key, item => item.Value);
        //}

        //public HuffmanCoding(IEnumerable<KeyValuePair<int, T>> occurenceValueEnumarable)
        //{
        //    _occurenceValue = occurenceValueEnumarable.ToDictionary(item => item.Key, item => item.Value);
        //}


        public IHuffmanNode<T> CreateTree()
        {
            var items = new List<IHuffmanNode<T>>(_occurenceValue);

            while (items.Count > 1)
            {
                items.Sort();
                var first = items[0];
                var second = items[1];
                items.RemoveAt(1);
                items.RemoveAt(0);
                var newParent = new HuffmanTreeNode<T>(first.Occurance + second.Occurance, first, second);
                items.Add(newParent);
            }
            return items.First();
        }
    }
}