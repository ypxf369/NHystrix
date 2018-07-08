using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using AspectCore.DynamicProxy;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NHystrix.Test
{
    [TestClass]
    public class HystrixCommandAttributeTest
    {
        [TestMethod]
        public void HystrixCommandTest()
        {
            ProxyGeneratorBuilder proxyGeneratorBuilder = new ProxyGeneratorBuilder();
            using (IProxyGenerator proxyGenerator = proxyGeneratorBuilder.Build())
            {
                TestObject p = proxyGenerator.CreateClassProxy<TestObject>();
                string text = p.Test1("yepeng");
                Assert.AreEqual(text, "降级的方法yepeng");
            }
        }
    }

    public class TestObject
    {
        [HystrixCommand(nameof(Test1Fallback), MaxRetryTimes = 3, EnableCircuitBreaker = true, CacheMilliseconds=100000)]
        public virtual string Test1(string name)
        {
            string s = null;
            s.ToString();
            return "ok" + name;
        }

        public virtual string Test1Fallback(string name)
        {
            return "降级的方法"+name;
        }
    }
}
