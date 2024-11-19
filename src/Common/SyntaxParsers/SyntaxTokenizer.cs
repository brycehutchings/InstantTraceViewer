using System.Text;

namespace InstantTraceViewer
{
    public static class SyntaxTokenizer
    {
        public static IEnumerable<string> Tokenize(string text)
        {
            bool IsDelimiter(char c) => c == '"' || c == '(' || c == ')' || char.IsWhiteSpace(c);

            int i = 0;
            while (i < text.Length)
            {
                // Skip whitespace
                for (; i < text.Length && char.IsWhiteSpace(text[i]); i++) ;

                if (i >= text.Length)
                {
                    break;
                }
                else if (text[i] == '"')
                {
                    int startIndex = i++;
                    for (; i < text.Length && text[i] != '"'; i++)
                    {
                        if (text[i] == '\\')
                        {
                            i++;
                        }
                    }
                    i++;
                    yield return i < text.Length ? text.Substring(startIndex, i - startIndex) : text.Substring(startIndex);
                }
                else if (text[i] == '(' || text[i] == ')')
                {
                    yield return text[i++].ToString();
                }
                else
                {
                    // Read a token until a delimiter.
                    int startIndex = i;
                    for (; i < text.Length && !IsDelimiter(text[i]); i++) ;
                    yield return text.Substring(startIndex, i - startIndex).ToString();
                }
            }
        }
    }
}
