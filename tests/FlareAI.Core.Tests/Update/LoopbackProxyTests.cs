using System.Net;
using FlareAI.Core.Update;
using Xunit;

namespace FlareAI.Core.Tests.Update
{
    public class LoopbackProxyTests
    {
        [Theory]
        [InlineData("http://127.0.0.1:8123/v1/messages")]
        [InlineData("http://localhost:8123/v1/messages")]
        [InlineData("http://[::1]:8123/v1/messages")]
        public void ARequestToThisComputer_GoesStraightThere(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            Assert.True(LoopbackProxy.Apply(req));
            Assert.Null(req.Proxy);
        }

        [Theory]
        [InlineData("https://api.github.com/repos/x/y/releases/latest")]
        [InlineData("https://api.anthropic.com/v1/messages")]
        [InlineData("http://gateway.example.com:8080/v1/chat/completions")]
        public void AnyOtherAddress_KeepsItsProxy(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            var before = req.Proxy;
            Assert.False(LoopbackProxy.Apply(req));
            Assert.Same(before, req.Proxy);
        }
    }
}
