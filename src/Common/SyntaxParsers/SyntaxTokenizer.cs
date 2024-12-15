using System.Text;

namespace InstantTraceViewer
{
    public record struct Token(string Text, int StartIndex);

    public static class SyntaxTokenizer
    {
        public const string EofText = "\0";

        public static IEnumerable<Token> Tokenize(string text)
        {
            bool IsDelimiter(char c) => c == '"' || c == '(' || c == ')' || char.IsWhiteSpace(c);

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
                else if (text[i] == '(' || text[i] == ')')
                {
                    yield return new Token(text[i++].ToString(), startIndex);
                }
                else
                {
                    // Read a token until a delimiter.
                    for (; i < text.Length && !IsDelimiter(text[i]); i++) ;
                    yield return new Token(text[startIndex..i], startIndex);
                }
            }

            // Return a special EOF token which makes it easier for the syntax parsers to provide next token suggestions.
            yield return new Token(EofText, text.Length);
        }
    }
}
