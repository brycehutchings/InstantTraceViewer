using System.Text;

namespace InstantTraceViewer
{
    public static class SyntaxTokenizer
    {
        public static IEnumerable<string> Tokenize(string text)
        {
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
                    StringBuilder sb = new();
                    sb.Append(text[i]); // Start quote
                    i++;
                    while (true)
                    {
                        if (i == text.Length)
                        {
                            throw new ArgumentException("Unterminated string literal");
                        }
                        if (text[i] == '\\')
                        {
                            i++;
                            if (i == text.Length)
                            {
                                throw new ArgumentException("Unterminated string literal");
                            }

                            sb.Append(text[i] switch
                            {
                                '"' => '"',
                                '\\' => '\\',
                                'n' => '\n',
                                't' => '\t',
                                'r' => '\r',
                                _ => throw new ArgumentException($"Invalid escape sequence: \\{text[i]}")
                            });
                            i++;
                        }
                        else if (text[i] == '"')
                        {
                            sb.Append(text[i]);
                            i++;
                            yield return sb.ToString();
                            break;
                        }
                        else
                        {
                            sb.Append(text[i]);
                            i++;
                        }
                    }
                }
                else if (text[i] == '(' || text[i] == ')')
                {
                    yield return text[i++].ToString();
                }
                else
                {
                    // Read a token until parenthesis or whitespace
                    int startIndex = i;
                    for (; i < text.Length && text[i] != '(' && text[i] != ')' && !char.IsWhiteSpace(text[i]); i++) ;
                    yield return text.Substring(startIndex, i - startIndex).ToString();
                }
            }
        }
    }
}
