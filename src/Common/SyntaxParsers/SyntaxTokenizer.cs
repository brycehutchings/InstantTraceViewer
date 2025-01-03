namespace InstantTraceViewer
{
    public record struct Token(string Text, int StartIndex);

    public static class SyntaxTokenizer
    {
        public const string EofText = "\0";

        public static IEnumerable<Token> Tokenize(string text)
        {
            // These characters are treated as individual tokens regardless of what's adjacent.
            bool IsSingleCharacterToken(char c) => c == '"' || c == '(' || c == ')' || c == '[' || c == ']' || c == ',';

            // These punctuation characters may be grouped together to form a single token.
            bool IsPunctuation(char c) => c == '=' || c == '"' || c == ',' || c == '<' || c == '>' || c == '!' || c == '~';

            // These characters are treated as delimiters when reading non-punctuation tokens.
            bool IsWordDelimiter(char c) => IsSingleCharacterToken(c) || IsPunctuation(c) || char.IsWhiteSpace(c);

            int i = 0;
            while (i < text.Length)
            {
                // Skip whitespace
                for (; i < text.Length && char.IsWhiteSpace(text[i]); i++) ;

                int startIndex = i;
                if (i >= text.Length)
                {
                    break;
                }
                else if (text[i] == '"')
                {
                    i++;
                    for (; i < text.Length && text[i] != '"'; i++)
                    {
                        if (text[i] == '\\') // Skip escape character.
                        {
                            i++;
                        }
                    }
                    i++;
                    string token = i < text.Length ? text[startIndex..i] : text[startIndex..];
                    yield return new Token(token, startIndex);
                }
                else if (IsSingleCharacterToken(text[i]))
                {
                    // There are some special multi-character tokens.
                    yield return new Token(text[i++].ToString(), startIndex);
                }
                else if (IsPunctuation(text[i]))
                {
                    // Read a token as long as it is a punctuation character (e.g. "<=")
                    for (; i < text.Length && IsPunctuation(text[i]); i++) ;
                    yield return new Token(text[startIndex..i], startIndex);
                }
                else
                {
                    // Read a token until a delimiter.
                    for (; i < text.Length && !IsWordDelimiter(text[i]); i++) ;
                    yield return new Token(text[startIndex..i], startIndex);
                }
            }

            // Return a special EOF token which makes it easier for the syntax parsers to provide next token suggestions.
            yield return new Token(EofText, text.Length);
        }
    }
}
