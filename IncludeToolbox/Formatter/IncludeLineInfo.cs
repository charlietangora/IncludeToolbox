﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace IncludeToolbox.Formatter
{
    [Flags]
    public enum ParseOptions
    {
        None = 0,

        /// <summary>
        /// Whether IncludeLineInfo objects should be created for empty lines.</param>
        /// </summary>
        RemoveEmptyLines = 1,

        /// <summary>
        /// Marks all includes that are within preprocessor conditionals as inactive/non-includes
        /// </summary>
        IgnoreIncludesInPreprocessorConditionals = 2,

        /// <summary>
        /// Keep only lines that contain valid includes.
        /// </summary>
        KeepOnlyValidIncludes = 4 | RemoveEmptyLines,
    }

    /// <summary>
    /// A line of text + information about the include directive in this line if any.
    /// Allows for manipulation of the former.
    /// </summary>
    /// <remarks>
    /// This is obviously not a high performance representation of text, but very easy to use for our purposes here.
    /// </remarks>
    public class IncludeLineInfo
    {
        /// <summary>
        /// Parses a given text into IncludeLineInfo objects.
        /// </summary>
        /// <returns>A list of parsed lines.</returns>
        public static List<IncludeLineInfo> ParseIncludes(string text, ParseOptions options)
        {
            StringReader reader = new StringReader(text);

            var outInfo = new List<IncludeLineInfo>();

            // Simplistic parsing.
            int openMultiLineComments = 0;
            int openIfdefs = 0;
            string lineText;
            while (true)
            {
                lineText = reader.ReadLine();
                if (lineText == null)
                    break;

                if (options.HasFlag(ParseOptions.RemoveEmptyLines) && string.IsNullOrWhiteSpace(lineText))
                    continue;

                int commentedSectionStart = int.MaxValue;
                int commentedSectionEnd = int.MaxValue;

                // Check for single line comment.
                {
                    int singleLineCommentStart = lineText.IndexOf("//");
                    if (singleLineCommentStart != -1)
                        commentedSectionStart = singleLineCommentStart;
                }

                // Check for multi line comments.
                {
                    int multiLineCommentStart = lineText.IndexOf("/*");
                    if (multiLineCommentStart > -1 && multiLineCommentStart < commentedSectionStart)
                    {
                        ++openMultiLineComments;
                        commentedSectionStart = multiLineCommentStart;
                    }
                    
                    int multiLineCommentEnd = lineText.IndexOf("*/");
                    if (multiLineCommentEnd > -1)
                    {
                        --openMultiLineComments;
                        commentedSectionEnd = multiLineCommentEnd;
                    }
                }

                Func<int, bool> isCommented = pos => (commentedSectionStart == int.MaxValue && openMultiLineComments > 0) || 
                                                     (pos > commentedSectionStart && pos < commentedSectionEnd);

                // Check for #if / #ifdefs.
                if (options.HasFlag(ParseOptions.IgnoreIncludesInPreprocessorConditionals))
                {
                    // There can be only a single preprocessor directive per line, so no need to parse more than this.
                    int ifdefStart = lineText.IndexOf("#if");
                    int ifdefEnd = lineText.IndexOf("#endif");
                    if (ifdefStart > -1 && !isCommented(ifdefStart))
                    {
                        ++openIfdefs;
                    }
                    else if (ifdefEnd > -1 && !isCommented(ifdefEnd))
                    {
                        --openIfdefs;
                    }
                }

                int includeOccurence = lineText.IndexOf("#include");

                // Not a valid include.
                if (includeOccurence == -1 ||        // Include not found 
                    isCommented(includeOccurence) || // Include commented out
                    openIfdefs > 0)                // Inside an #ifdef block
                {
                    if (!options.HasFlag(ParseOptions.KeepOnlyValidIncludes))
                        outInfo.Add(new IncludeLineInfo() { lineText = lineText });
                }
                // A valid include
                else
                {
                    // Parse include delimiters.
                    int delimiter1 = -1;
                    int delimiter0 = lineText.IndexOf('\"', includeOccurence + "#include".Length);
                    if (delimiter0 == -1)
                    {
                        delimiter0 = lineText.IndexOf('<', includeOccurence + "#include".Length);
                        if (delimiter0 != -1)
                            delimiter1 = lineText.IndexOf('>', delimiter0 + 1);
                    }
                    else
                    {
                        delimiter1 = lineText.IndexOf('\"', delimiter0 + 1);
                    }

                    // Might not be valid after all!
                    if (delimiter0 != -1 && delimiter1 != -1)
                        outInfo.Add(new IncludeLineInfo() { lineText = lineText, delimiter0 = delimiter0, delimiter1 = delimiter1 });
                    else if(!options.HasFlag(ParseOptions.KeepOnlyValidIncludes))
                        outInfo.Add(new IncludeLineInfo() { lineText = lineText });
                }
            }

            return outInfo;
        }


        public enum Type
        {
            Quotes,
            AngleBrackets,
            NoInclude
        }

        public Type LineType
        {
            get
            {
                if (ContainsActiveInclude)
                {
                    if (lineText[delimiter0] == '<')
                        return Type.AngleBrackets;
                    else if (lineText[delimiter0] == '\"')
                        return Type.Quotes;
                }

                return Type.NoInclude;
            }
        }

        /// <summary>
        /// Whether the line includes an enabled include.
        /// </summary>
        /// <remarks>
        /// A line that contains a valid #include may still be ContainsActiveInclude==false if it is commented or (depending on parsing options) #if(def)'ed out.
        /// </remarks>
        public bool ContainsActiveInclude
        {
            get { return delimiter0 != -1; }
        }

        /// <summary>
        /// Changes the type of this line.
        /// </summary>
        /// <param name="newLineType">Type.NoInclude won't have any effect.</param>
        public void SetLineType(Type newLineType)
        {
            if (LineType != newLineType)
            {
                if (newLineType == Type.AngleBrackets)
                {
                    StringBuilder sb = new StringBuilder(lineText);
                    sb[delimiter0] = '<';
                    sb[delimiter1] = '>';
                    lineText = sb.ToString();
                }
                else if (newLineType == Type.Quotes)
                {
                    StringBuilder sb = new StringBuilder(lineText);
                    sb[delimiter0] = '"';
                    sb[delimiter1] = '"';
                    lineText = sb.ToString();
                }
            }
        }


        /// <summary>
        /// Tries to resolve the include (if any) using a list of directories.
        /// </summary>
        /// <param name="includeDirectories">Include directories.</param>
        /// <returns>Empty string if this is not an include, absolute include path if possible or raw include if not.</returns>
        public string TryResolveInclude(IEnumerable<string> includeDirectories)
        {
            if (!ContainsActiveInclude)
                return "";

            string includeContent = IncludeContent;

            foreach (string dir in includeDirectories)
            {
                string candidate = Path.Combine(dir, includeContent);
                if (File.Exists(candidate))
                {
                    return Utils.GetExactPathName(candidate);
                }
            }

            Output.Instance.WriteLine("Unable to resolve include: '{0}'", includeContent);
            return includeContent;
        }

        /// <summary>
        /// Include content with added delimiters.
        /// </summary>
        public string GetIncludeContentWithDelimiters()
        {
            return lineText.Substring(delimiter0, delimiter1 - delimiter0 + 1);
        }


        /// <summary>
        /// Changes in the include content will NOT be reflected immediately in the raw line text. 
        /// </summary>
        /// <see cref="UpdateRawLineWithIncludeContentChanges"/>
        public string IncludeContent
        {
            get
            {
                int length = delimiter1 - delimiter0 - 1;
                return length > 0 ? RawLine.Substring(delimiter0 + 1, length) : "";
            }
            set
            {
                if (!ContainsActiveInclude)
                    return;

                lineText = lineText.Remove(delimiter0 + 1, delimiter1 - delimiter0 - 1);
                lineText = lineText.Insert(delimiter0 + 1, value);
                delimiter1 = delimiter0 + value.Length + 1;
            }
        }

        /// <summary>
        /// Raw line text as found.
        /// </summary>
        public string RawLine
        {
            get { return lineText; }
        }
        private string lineText = "";

        private int delimiter0 = -1;
        private int delimiter1 = -1;
    }
}
