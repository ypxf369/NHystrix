using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using AspectCore.DynamicProxy;
using Microsoft.Extensions.Caching.Memory;
using Polly;

namespace NHystrix
{
    [AttributeUsage(AttributeTargets.Method)]
    public class HystrixCommandAttribute : AbstractInterceptorAttribute
    {
        /// <summary>
        /// 最多重试几次，如果为0则不重试
        /// </summary>
        public int MaxRetryTimes { get; set; } = 0;

        /// <summary>
        /// 重试间隔毫秒数
        /// </summary>
        public int RetryIntervalMilliseconds { get; set; } = 100;

        /// <summary>
        /// 是否启用熔断
        /// </summary>
        public bool EnableCircuitBreaker { get; set; } = false;

        /// <summary>
        /// 熔断前允许出现几次错误
        /// </summary>
        public int ExceptionsAllowedBeforeBreaking { get; set; } = 3;

        /// <summary>
        /// 熔断多长时间
        /// </summary>
        public int MillisecondsOfBread { get; set; } = 1000;

        /// <summary>
        /// 执行超多多少毫秒则认为超市（0表示不检测超时）
        /// </summary>
        public int TimeOutMilliseconds { get; set; } = 0;

        /// <summary>
        /// 缓存多少毫秒（0表示不缓存），用“类名+方法名+所有参数ToString拼接”做缓存Key
        /// </summary>
        public int CacheMilliseconds { get; set; } = 0;

        private Policy _policy;

        private static readonly IMemoryCache MemoryCache = new MemoryCache(new MemoryCacheOptions());

        public HystrixCommandAttribute(string fallBackMethod)
        {
            FallBackMethod = fallBackMethod;
        }

        /// <summary>
        /// 降级的方法名
        /// </summary>
        public string FallBackMethod { get; set; }

        public override async Task Invoke(AspectContext context, AspectDelegate next)
        {
            //一个HystrixCommand中保持一个policy对象即可
            //其实主要是CircuitBreaker要求对于同一段代码要共享一个policy对象
            //根据反射原理，同一个方法就对应一个HystrixCommandAttribute，无论几次调用，
            //而不同方法对应不同的HystrixCommandAttribute对象，天然的一个policy对象共享
            //因为同一个方法共享一个policy，因此这个CircuitBreaker是针对所有请求的。
            //Attribute也不会在运行时再去改变属性的值，共享同一个policy对象也没问题

            lock (this) //因为Invoke可能是并发调用，因此要确保policy赋值的线程安全
            {
                if (_policy == null)
                {
                    _policy = Policy.NoOpAsync();//创建一个空的Policy
                    if (EnableCircuitBreaker)
                    {
                        _policy =
                            _policy.WrapAsync(
                                Policy.Handle<Exception>()
                                    .CircuitBreakerAsync(ExceptionsAllowedBeforeBreaking,
                                        TimeSpan.FromMilliseconds(MillisecondsOfBread)));
                    }
                    if (TimeOutMilliseconds > 0)
                    {
                        _policy =
                            _policy.WrapAsync(Policy.TimeoutAsync(() => TimeSpan.FromMilliseconds(TimeOutMilliseconds),
                                Polly.Timeout.TimeoutStrategy.Pessimistic));
                    }
                    if (MaxRetryTimes > 0)
                    {
                        _policy =
                            _policy.WrapAsync(Policy.Handle<Exception>()
                                .WaitAndRetryAsync(MaxRetryTimes,
                                    i => TimeSpan.FromMilliseconds(RetryIntervalMilliseconds)));
                    }
                    Policy policyFallBack = Policy.Handle<Exception>().FallbackAsync((ctx, t) => Task.Run(() =>
                    {
                        //这里拿到的就是ExecuteAsync(ctx=>next(context),pollyCtx);这里传的pollyCtx
                        AspectContext aspectContext = (AspectContext)ctx["aspectContext"];
                        var fallBackMethod = context.ServiceMethod.DeclaringType?.GetMethod(FallBackMethod);
                        var fallBackResult = fallBackMethod?.Invoke(context.Implementation, context.Parameters);
                        //不能如下这样，因为这是闭包相关，如果这样写第二次调用Invoke的时候context指向的
                        //还是第一次的对象，所以要通过Polly的上下文来传递AspectContext
                        //context.ReturnValue = fallBackResult;
                        aspectContext.ReturnValue = fallBackResult;
                    }, t), (ex, t) => Task.FromResult(0));

                    _policy = policyFallBack.WrapAsync(_policy);
                }
            }

            //把本地调用的AspectContext传递给Polly，主要给FallbackAsync中使用，避免闭包的坑
            Context pollyCtx = new Context//Context是polly中通过Execute给FallBack、Execute等回调方法传上下文对象使用的
            {
                ["aspectContext"] = context//context是aspectCore的上下文
            };

            if (CacheMilliseconds > 0)
            {
                //用类名+方法名+参数的下划线链接起来作为缓存Key
                string cacheKey = $"HystrixMethodCacheManager_Key_{context.ServiceMethod.DeclaringType}.{context.ServiceMethod}.{string.Join("_", context.Parameters)}";
                //尝试去缓存中获取。如果找到了，则直接用缓存中的值做返回值
                if (MemoryCache.TryGetValue(cacheKey, out var cacheValue))
                {
                    context.ReturnValue = cacheValue;
                }
                else
                {
                    //如果缓存中没有，则执行实际被拦截的方法
                    await _policy.ExecuteAsync(i => next(context), pollyCtx);
                    //存入缓存
                    using (var cacheEntry = MemoryCache.CreateEntry(cacheKey))
                    {
                        cacheEntry.Value = context.ReturnValue; //返回值放入缓存
                        cacheEntry.AbsoluteExpiration = DateTime.Now + TimeSpan.FromMilliseconds(CacheMilliseconds);
                    }
                }
            }
            else//如果没有启用缓存，就直接执行业务方法
            {
                await _policy.ExecuteAsync(i => next(context), pollyCtx);
            }
        }
    }
}
