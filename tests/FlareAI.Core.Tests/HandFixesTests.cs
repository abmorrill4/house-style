using System;
using System.Xml;
using FlareAI.Core.Actions;
using Xunit;

namespace FlareAI.Core.Tests
{
    public class HandFixesTests
    {
        private static XmlDocument Parse(string xml)
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            return doc;
        }

        [Fact]
        public void Count_IsLiteral_ForRegexCharacters()
        {
            Assert.Equal(2, HandFixes.Count("a (see [Note b (see [Note", "(see [Note"));
        }

        [Fact]
        public void Count_DoesNotOverlap()
        {
            Assert.Equal(2, HandFixes.Count("aaaa", "aa"));
        }

        [Fact]
        public void Count_NullOrEmpty_IsZero()
        {
            Assert.Equal(0, HandFixes.Count(null, "x"));
            Assert.Equal(0, HandFixes.Count("x", ""));
            Assert.Equal(0, HandFixes.Count("x", null));
        }

        [Fact]
        public void ContextKey_TakesTheLast12CharactersOfBefore()
        {
            var f = new StyleFinding { Before = "0123456789abcdef", Excerpt = "x" };
            Assert.Equal("456789abcdefx", HandFixes.ContextKey(f));

            var none = new StyleFinding { Before = null, Excerpt = "x" };
            Assert.Equal("x", HandFixes.ContextKey(none));
        }

        [Fact]
        public void ScopeText_JoinsRunsSplitByInlineMarkup()
        {
            var doc = Parse("<body><p>Please <b>utilize</b> it</p></body>");
            var p = doc.DocumentElement.FirstChild;
            Assert.Equal("Please utilize it", HandFixes.ScopeText(p));
        }

        [Fact]
        public void ScopeText_NullScope_IsEmpty()
        {
            Assert.Equal("", HandFixes.ScopeText(null));
        }

        [Fact]
        public void IsAttached_FalseForARemovedBlock()
        {
            var doc = Parse("<body><p>One</p><p>Two</p></body>");
            var p = doc.DocumentElement.FirstChild;
            var f = new StyleFinding { Block = p };
            Assert.True(HandFixes.IsAttached(f));

            doc.DocumentElement.RemoveChild(p);
            Assert.False(HandFixes.IsAttached(f));
        }

        [Fact]
        public void IsAttached_FalseWhenBlockIsNull()
        {
            Assert.False(HandFixes.IsAttached(new StyleFinding { Block = null }));
        }

        [Fact]
        public void SamePath_IgnoresCaseAndSeparators()
        {
            Assert.True(HandFixes.SamePath(@"C:\A\b.htm", "c:/a/B.htm"));
            Assert.False(HandFixes.SamePath(null, "c:/a/B.htm"));
            Assert.False(HandFixes.SamePath(@"C:\A\b.htm", null));
            Assert.False(HandFixes.SamePath(null, null));
        }

        [Fact]
        public void SamePath_UncPathDoesNotThrow()
        {
            Assert.False(HandFixes.SamePath(@"\\server\share\x.htm", @"C:\A\b.htm"));
            Assert.True(HandFixes.SamePath(@"\\server\share\x.htm", @"\\server\share\x.htm"));
        }
    }
}
