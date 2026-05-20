using System;
using System.Globalization;

namespace DriverScanTester.ViewModels
{
    internal interface IAddressExpression
    {
        bool UsesPointer { get; }
        bool TryResolve(LockAddressEntryViewModel owner, out ulong value);
    }

    internal sealed class ConstantExpression : IAddressExpression
    {
        private readonly ulong _value;

        public ConstantExpression(ulong value)
        {
            _value = value;
        }

        public bool UsesPointer => false;

        public bool TryResolve(LockAddressEntryViewModel owner, out ulong value)
        {
            value = _value;
            return true;
        }
    }

    internal sealed class ModuleExpression : IAddressExpression
    {
        private readonly string _moduleName;
        private ulong _cachedBase;

        public ModuleExpression(string moduleName)
        {
            _moduleName = moduleName;
        }

        public bool UsesPointer => false;

        public bool TryResolve(LockAddressEntryViewModel owner, out ulong value)
        {
            if (_cachedBase != 0)
            {
                value = _cachedBase;
                return true;
            }

            value = owner.ResolveModule(_moduleName);
            if (value != 0)
            {
                _cachedBase = value;
                return true;
            }
            return false;
        }
    }

    internal sealed class PointerExpression : IAddressExpression
    {
        private readonly IAddressExpression _inner;

        public PointerExpression(IAddressExpression inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public bool UsesPointer => true;

        public bool TryResolve(LockAddressEntryViewModel owner, out ulong value)
        {
            value = 0;
            if (!_inner.TryResolve(owner, out var address))
                return false;

            return owner.TryReadPointer(address, out value);
        }
    }

    internal sealed class BinaryExpression : IAddressExpression
    {
        private readonly IAddressExpression _left;
        private readonly IAddressExpression _right;
        private readonly int _sign;

        public BinaryExpression(IAddressExpression left, IAddressExpression right, int sign)
        {
            _left = left ?? throw new ArgumentNullException(nameof(left));
            _right = right ?? throw new ArgumentNullException(nameof(right));
            _sign = sign;
            UsesPointer = left.UsesPointer || right.UsesPointer;
        }

        public bool UsesPointer { get; }

        public bool TryResolve(LockAddressEntryViewModel owner, out ulong value)
        {
            value = 0;
            if (!_left.TryResolve(owner, out var leftVal))
                return false;
            if (!_right.TryResolve(owner, out var rightVal))
                return false;

            try
            {
                checked
                {
                    value = _sign >= 0
                        ? leftVal + rightVal
                        : leftVal - rightVal;
                }
                return true;
            }
            catch (OverflowException)
            {
                return false;
            }
        }
    }

    internal sealed class AddressExpressionParser
    {
        private readonly string _text;
        private int _index;

        private AddressExpressionParser(string text)
        {
            _text = text;
            _index = 0;
        }

        public static bool TryParse(string text, out IAddressExpression expression)
        {
            expression = default!;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var parser = new AddressExpressionParser(text);
            parser.SkipWhitespace();
            if (!parser.TryParseExpression(out expression))
                return false;

            parser.SkipWhitespace();
            return parser._index >= parser._text.Length;
        }

        private bool TryParseExpression(out IAddressExpression expression)
        {
            if (!TryParseTerm(out expression))
                return false;

            while (true)
            {
                SkipWhitespace();
                if (Match('+'))
                {
                    if (!TryParseTerm(out var right))
                        return false;
                    expression = new BinaryExpression(expression, right, +1);
                }
                else if (Match('-'))
                {
                    if (!TryParseTerm(out var right))
                        return false;
                    expression = new BinaryExpression(expression, right, -1);
                }
                else
                {
                    break;
                }
            }

            return true;
        }

        private bool TryParseTerm(out IAddressExpression expression)
        {
            SkipWhitespace();
            if (Match('['))
            {
                if (!TryParseExpression(out var inner))
                {
                    expression = default!;
                    return false;
                }
                SkipWhitespace();
                if (!Match(']'))
                {
                    expression = default!;
                    return false;
                }
                expression = new PointerExpression(inner);
                return true;
            }

            if (TryParseNumber(out ulong value))
            {
                expression = new ConstantExpression(value);
                return true;
            }

            if (TryParseIdentifier(out string identifier))
            {
                expression = new ModuleExpression(identifier);
                return true;
            }

            expression = default!;
            return false;
        }

        private bool TryParseIdentifier(out string identifier)
        {
            identifier = null!;
            SkipWhitespace();
            int start = _index;

            // Must start with letter or underscore
            char c = Peek();
            if (!char.IsLetter(c) && c != '_')
                return false;

            _index++;
            while (_index < _text.Length)
            {
                c = _text[_index];
                if (char.IsLetterOrDigit(c) || c == '_' || c == '.')
                {
                    _index++;
                }
                else
                {
                    break;
                }
            }

            identifier = _text.Substring(start, _index - start);
            return true;
        }

        private bool TryParseNumber(out ulong value)
        {
            SkipWhitespace();
            int start = _index;
            if (Peek() == '0' && (Peek(1) == 'x' || Peek(1) == 'X'))
            {
                _index += 2;
                int digitsStart = _index;
                while (IsHexDigit(Peek()))
                    _index++;
                if (_index == digitsStart)
                {
                    _index = start;
                    value = 0;
                    return false;
                }

                var token = _text.Substring(digitsStart, _index - digitsStart);
                return ulong.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
            }

            while (IsHexDigit(Peek()))
                _index++;

            if (_index == start)
            {
                value = 0;
                return false;
            }

            // Check if we stopped in the middle of a word
            if (_index < _text.Length && (char.IsLetterOrDigit(_text[_index]) || _text[_index] == '_' || _text[_index] == '.'))
            {
                _index = start;
                value = 0;
                return false;
            }

            var span = _text.Substring(start, _index - start);
            if (HasHexLetters(span))
                return ulong.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

            return ulong.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private void SkipWhitespace()
        {
            while (_index < _text.Length && char.IsWhiteSpace(_text[_index]))
                _index++;
        }

        private bool Match(char c)
        {
            if (_index < _text.Length && _text[_index] == c)
            {
                _index++;
                return true;
            }

            return false;
        }

        private char Peek(int offset = 0)
        {
            int idx = _index + offset;
            return idx >= 0 && idx < _text.Length ? _text[idx] : '\0';
        }

        private static bool IsHexDigit(char c)
            => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

        private static bool HasHexLetters(string s)
        {
            foreach (char c in s)
            {
                if (char.IsLetter(c))
                    return true;
            }

            return false;
        }
    }
}
