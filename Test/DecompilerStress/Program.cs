using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;

namespace DecompilerStress
{
    internal static class Program
    {
        private static async Task Main()
        {
            var customers = new[]
            {
                new { Id = 1, Name = "Alice", Region = "West", Tier = 2 },
                new { Id = 2, Name = "Bob", Region = "North", Tier = 1 },
                new { Id = 3, Name = "Charlie", Region = "West", Tier = 3 },
                new { Id = 4, Name = "Diana", Region = "South", Tier = 2 },
                new { Id = 5, Name = "Eve", Region = "North", Tier = 3 }
            };

            var products = new[]
            {
                new { Id = 10, Name = "Laptop", Category = "Tech", Price = 1200m },
                new { Id = 11, Name = "Mouse", Category = "Tech", Price = 40m },
                new { Id = 12, Name = "Desk", Category = "Office", Price = 350m },
                new { Id = 13, Name = "Chair", Category = "Office", Price = 220m }
            };

            var orders = new[]
            {
                new { Id = 100, CustomerId = 1, ProductId = 10, Quantity = 1 },
                new { Id = 101, CustomerId = 1, ProductId = 11, Quantity = 3 },
                new { Id = 102, CustomerId = 2, ProductId = 12, Quantity = 2 },
                new { Id = 103, CustomerId = 3, ProductId = 10, Quantity = 1 },
                new { Id = 104, CustomerId = 3, ProductId = 13, Quantity = 4 },
                new { Id = 105, CustomerId = 5, ProductId = 11, Quantity = 5 },
                new { Id = 106, CustomerId = 5, ProductId = 12, Quantity = 1 }
            };

            // 1. Nested anonymous types + joins + computed properties.
            var enriched =
                orders
                    .Join(customers,
                        o => o.CustomerId,
                        c => c.Id,
                        (o, c) => new
                        {
                            Order = o,
                            Customer = c
                        })
                    .Join(products,
                        x => x.Order.ProductId,
                        p => p.Id,
                        (x, p) => new
                        {
                            x.Order.Id,
                            Customer = new
                            {
                                x.Customer.Id,
                                x.Customer.Name,
                                x.Customer.Region,
                                x.Customer.Tier
                            },
                            Product = new
                            {
                                p.Id,
                                p.Name,
                                p.Category,
                                p.Price
                            },
                            x.Order.Quantity,
                            Total = x.Order.Quantity * p.Price,
                            IsLarge = x.Order.Quantity * p.Price >= 500m,
                            Label = $"{x.Customer.Name}:{p.Name}"
                        })
                    .Where(x => x.Customer.Tier >= 2)
                    .OrderByDescending(x => x.Total)
                    .Select((x, index) => new
                    {
                        Position = index + 1,
                        x.Id,
                        x.Label,
                        x.Customer,
                        x.Product,
                        x.Quantity,
                        x.Total,
                        x.IsLarge,
                        Discounted = x.Total *
                            (x.Customer.Tier == 3 ? 0.85m : 0.95m)
                    })
                    .ToArray();

            Dump("1. Enriched orders", enriched);

            // 2. Grouping anonymous objects into nested aggregate projections.
            var regionalReport = enriched
                .GroupBy(x => new
                {
                    x.Customer.Region,
                    Priority = x.Customer.Tier >= 3 ? "High" : "Normal"
                })
                .Select(g => new
                {
                    Group = g.Key,
                    OrderCount = g.Count(),
                    CustomerCount = g.Select(x => x.Customer.Id)
                                     .Distinct()
                                     .Count(),
                    Revenue = g.Sum(x => x.Total),
                    AverageOrder = g.Average(x => (double)x.Total),
                    Expensive = g.Where(x => x.IsLarge)
                        .Select(x => new
                        {
                            x.Label,
                            x.Total,
                            x.Product.Category
                        })
                        .ToList(),
                    Summary = new
                    {
                        First = g.OrderBy(x => x.Total)
                                 .Select(x => x.Label)
                                 .FirstOrDefault(),
                        Last = g.OrderByDescending(x => x.Total)
                                .Select(x => x.Label)
                                .FirstOrDefault()
                    }
                })
                .OrderByDescending(x => x.Revenue)
                .ToList();

            Dump("2. Regional report", regionalReport);

            // 3. GroupJoin, DefaultIfEmpty and conditional projections.
            var customerReport = customers
                .GroupJoin(orders,
                    c => c.Id,
                    o => o.CustomerId,
                    (c, customerOrders) => new
                    {
                        Customer = c,
                        Orders = customerOrders
                    })
                .SelectMany(
                    x => x.Orders.DefaultIfEmpty(),
                    (x, order) => new
                    {
                        x.Customer.Id,
                        x.Customer.Name,
                        x.Customer.Region,
                        OrderId = order == null ? (int?)null : order.Id,
                        ProductId = order == null ? (int?)null : order.ProductId,
                        Quantity = order?.Quantity ?? 0
                    })
                .GroupBy(x => new { x.Region, x.Name })
                .Select(g => new
                {
                    g.Key.Region,
                    g.Key.Name,
                    OrderIds = g.Where(x => x.OrderId.HasValue)
                                .Select(x => x.OrderId!.Value)
                                .ToArray(),
                    ProductIds = g.Where(x => x.ProductId.HasValue)
                                  .Select(x => x.ProductId!.Value)
                                  .Distinct()
                                  .ToList(),
                    Units = g.Sum(x => x.Quantity),
                    Status = g.Sum(x => x.Quantity) switch
                    {
                        0 => "Inactive",
                        < 4 => "Small",
                        < 8 => "Medium",
                        _ => "Large"
                    }
                })
                .Select(x => new
                {
                    x.Region,
                    x.Name,
                    x.OrderIds,
                    x.ProductIds,
                    x.Units,
                    x.Status,
                    Metadata = new
                    {
                        Key = $"{x.Region}/{x.Name}",
                        Hash = HashCode.Combine(x.Name, x.Region, x.Units),
                        HasOrders = x.OrderIds.Length != 0
                    }
                })
                .ToArray();

            Dump("3. Customer report", customerReport);

            // 4. Query syntax, let clauses, nested projections and grouping.
            var querySyntax =
                (from o in orders
                 join c in customers on o.CustomerId equals c.Id
                 join p in products on o.ProductId equals p.Id
                 let gross = o.Quantity * p.Price
                 let segment = c.Tier switch
                 {
                     1 => "Bronze",
                     2 => "Silver",
                     _ => "Gold"
                 }
                 where gross > 100m
                 group new
                 {
                     Order = o,
                     Customer = c,
                     Product = p,
                     Gross = gross,
                     Segment = segment
                 }
                 by new { c.Region, Segment = segment } into grp
                 orderby grp.Sum(x => x.Gross) descending
                 select new
                 {
                     grp.Key.Region,
                     grp.Key.Segment,
                     Count = grp.Count(),
                     Total = grp.Sum(x => x.Gross),
                     Items = grp.Select(x => new
                     {
                         x.Customer.Name,
                         Product = x.Product.Name,
                         x.Gross,
                         x.Order.Quantity
                     }).ToArray()
                 }).ToList();

            Dump("4. Query syntax", querySyntax);

            // 5. Closures capturing a changing variable.
            int threshold = 100;

            Func<decimal, bool> isAboveThreshold = value =>
                value > threshold;

            var before = enriched
                .Where(x => isAboveThreshold(x.Total))
                .Select(x => new { x.Label, x.Total })
                .ToList();

            threshold = 500;

            var after = enriched
                .Where(x => isAboveThreshold(x.Total))
                .Select(x => new { x.Label, x.Total })
                .ToList();

            Dump("5a. Closure before", before);
            Dump("5b. Closure after", after);

            // 6. Local function returning anonymous objects via generics.
            var transformed = Transform(enriched,
                x => new
                {
                    Key = x.Label.ToUpperInvariant(),
                    Amount = x.Total,
                    Bucket = x.Total switch
                    {
                        < 100m => 0,
                        < 500m => 1,
                        _ => 2
                    },
                    Info = new
                    {
                        x.Customer.Name,
                        x.Product.Category,
                        IsPreferred = x.Customer.Tier == 3
                    }
                },
                x => x.Amount + x.Bucket * 10m);

            Dump("6. Generic transformation", transformed);

            // 7. Anonymous objects, tuples and dictionary lookups.
            var lookup = products.ToDictionary(
                p => p.Id,
                p => new
                {
                    p.Name,
                    p.Category,
                    PriceWithTax = p.Price * 1.15m
                });

            var tupleProjection = orders.Select(o =>
            {
                var found = lookup.TryGetValue(o.ProductId, out var product);

                return (
                    Order: o.Id,
                    Details: new
                    {
                        Name = found ? product!.Name : "Unknown",
                        Category = found ? product!.Category : "Unknown",
                        Value = found ? product!.PriceWithTax * o.Quantity : 0m
                    }
                );
            }).ToList();

            Dump("7. Tuple projection", tupleProjection);

            // 8. Expression trees and dynamically composed predicates.
            Expression<Func<decimal, bool>> expensive = x => x >= 300m;
            Expression<Func<decimal, bool>> affordable = x => x < 1000m;

            var parameter = Expression.Parameter(typeof(decimal), "amount");

            var combined = Expression.Lambda<Func<decimal, bool>>(
                Expression.AndAlso(
                    Expression.Invoke(expensive, parameter),
                    Expression.Invoke(affordable, parameter)),
                parameter);

            var compiledPredicate = combined.Compile();

            var filtered = enriched
                .Where(x => compiledPredicate(x.Total))
                .Select(x => new
                {
                    x.Label,
                    x.Total,
                    Result = compiledPredicate(x.Total) ? "Accepted" : "Rejected"
                })
                .ToArray();

            Dump("8. Expression tree", filtered);

            // 9. Async query pipeline with anonymous return types.
            var asyncReport = await BuildReportAsync(enriched);

            Dump("9. Async report", asyncReport);

            // 10. Reflection over compiler-generated anonymous types.
            var sample = enriched.First();

            Type type = sample.GetType();

            Console.WriteLine("\n10. Anonymous type reflection");
            Console.WriteLine($"Type: {type.FullName}");
            Console.WriteLine($"Compiler generated: {type.IsDefined(
                typeof(CompilerGeneratedAttribute), false)}");

            foreach (var property in type.GetProperties())
            {
                object? value = property.GetValue(sample);

                Console.WriteLine(
                    $"{property.Name} : {property.PropertyType.Name} = {value}");
            }

            Console.WriteLine("\nAll tests completed.");
        }

        // Generic methods can carry anonymous types without naming them.
        private static List<TResult> Transform<TSource, TResult, TKey>(
            IEnumerable<TSource> source,
            Func<TSource, TResult> selector,
            Func<TResult, TKey> keySelector)
            where TKey : IComparable<TKey>
        {
            return source
                .Select(selector)
                .OrderBy(keySelector)
                .ToList();
        }

        private static async Task<List<TResult>> BuildReportAsync<TResult>(
            IEnumerable<TResult> source)
        {
            await Task.Delay(5).ConfigureAwait(false);

            return source
                .Select((x, i) => new { Index = i, Value = x })
                .Where(x => x.Index % 2 == 0)
                .Select(x => x.Value)
                .ToList();
        }

        private static void Dump<T>(string title, IEnumerable<T> items)
        {
            Console.WriteLine($"\n--- {title} ---");

            foreach (var item in items)
                Console.WriteLine(JsonSerializer.Serialize(item));
        }
    }
}
