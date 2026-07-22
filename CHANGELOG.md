# Changelog

## [5.8.0](https://github.com/eQuantic/core-data/compare/v5.7.0...v5.8.0) (2026-07-22)

### Features

* **core,relational,mongodb,cassandra,cosmosdb:** live-schema evolution and rich index options in the fluent migrations ([abc00f1](https://github.com/eQuantic/core-data/commit/abc00f1ee1f5cffa950e35cd5d72b479cd85cea5))
* **core,relational,mongodb,cosmosdb,cassandra:** entity lifecycle by convention and relational optimistic concurrency ([2f6f3f7](https://github.com/eQuantic/core-data/commit/2f6f3f7a078e25604633863c522fca25dfceba51))
* **core,relational,mongodb,cosmosdb,cassandra:** store-neutral modeling annotations with explicit precedence and a model Explain ([c7d7ffe](https://github.com/eQuantic/core-data/commit/c7d7ffeb2df41cb2b39d484ec4d9f6b91ffc9b59))
* **core:** DataConventions — tunable lifecycle conventions with who-stamps for eQuantic.Core.DataModel shapes ([f47922c](https://github.com/eQuantic/core-data/commit/f47922cfd8458e6c297f48eaa2c3f345c5f49429))
* **postgresql,mysql,cassandra,cosmosdb:** jsonb documents, MariaDB dialect, SASI LIKE pushdown and Cosmos GroupBy ([226484d](https://github.com/eQuantic/core-data/commit/226484d0104bf8c4b5e0117d5aa6d6a09151e2cf))
* **relational:** declared navigations with nested includes, and opt-in transient-fault retries ([820bae5](https://github.com/eQuantic/core-data/commit/820bae5a66277db4930e55bfde85ed24fe3fa159))
* **relational:** value converters — Value Objects and enums-as-strings mapped to stored scalars ([8dd1a35](https://github.com/eQuantic/core-data/commit/8dd1a35cb2f8909ca3c8fbe3800a707424a355a4))

### Bug Fixes

* **cosmosdb:** reject GroupBy honestly instead of emitting invalid SQL ([d46ecea](https://github.com/eQuantic/core-data/commit/d46ecea253edea797049de385e65dd83452abb60))

## [5.7.0](https://github.com/eQuantic/core-data/compare/v5.6.1...v5.7.0) (2026-07-22)

### Features

* **core,relational,cassandra:** complex-query surface — LIKE pushdown, Min/Max/Avg, relational Include and FromSql ([1ed14f9](https://github.com/eQuantic/core-data/commit/1ed14f9daafac8ececb9e74ce3c92f72aae28a93))
* **core,relational:** extensible database functions — Db markers, FunctionFilter and the dialect registry ([e269dc9](https://github.com/eQuantic/core-data/commit/e269dc951b3b0422a8173cccc8c9793d7d20106b))
* **core,relational:** typed GroupBy with server-side aggregate projection ([6ef627c](https://github.com/eQuantic/core-data/commit/6ef627ceb1cff50bf29ce7fb862bea16ed8cc3d6))
* **core,relational:** typed UNION/UNION ALL across entities and HAVING for grouped reads ([8d57354](https://github.com/eQuantic/core-data/commit/8d57354d097feb6985e6cb83b4c31696a24a167f))
* **mongodb,cassandra,cosmosdb:** grouped, union and aggregate reads across the document and wide-column providers ([a7d14a3](https://github.com/eQuantic/core-data/commit/a7d14a3c5c6ed0d7e0bcc9016054d0fc4af3b681))
* **mysql,sqlserver:** native MySQL and SQL Server providers as dialects ([6439b63](https://github.com/eQuantic/core-data/commit/6439b63be2627f54e7e7dd798cfb112949ddfe35))
* **postgresql:** native PostgreSQL provider over a shared relational engine ([c322a64](https://github.com/eQuantic/core-data/commit/c322a64a3c500f913cb34967ca0ea2b83692b7b4))

## [5.6.1](https://github.com/eQuantic/core-data/compare/v5.6.0...v5.6.1) (2026-07-21)

### Performance Improvements

* **core:** evaluate one-shot parameter-free folds by interpretation ([5e74d6a](https://github.com/eQuantic/core-data/commit/5e74d6aa9b4462639531b9dc4182d92b565cac3f))

## [5.6.0](https://github.com/eQuantic/core-data/compare/v5.5.0...v5.6.0) (2026-07-21)

### Features

* **cassandra:** counter columns and IF NOT EXISTS lightweight transactions ([f456c5e](https://github.com/eQuantic/core-data/commit/f456c5e72761f436546f6614c2e40ce715458087))
* **cassandra:** OR-split — partition-pinned OR branches run as parallel native queries ([e705fd6](https://github.com/eQuantic/core-data/commit/e705fd610b02657a7fef7507af11882aa441dc07))
* **core,cassandra,cosmosdb:** continuation-token paging over the native paths ([7b51e20](https://github.com/eQuantic/core-data/commit/7b51e208474b0da9c74a3595ba92ffef68b212d0))
* **core,providers:** global query filters, OpenTelemetry activities, per-operation consistency/TTL ([e99509a](https://github.com/eQuantic/core-data/commit/e99509aa0e0fa65fa630d221bb2710341bddd71d))
* **core,providers:** pushdown+residual engine, Explain, prepared statements ([8541a0b](https://github.com/eQuantic/core-data/commit/8541a0b6916eb04d5777f372ef006e40d337bca1))
* **core:** evaluate parameter-free filter operands at translation time ([1d09d11](https://github.com/eQuantic/core-data/commit/1d09d11600dfb76a4c7ef96a77b0cbdc7bff9286))
* **cosmosdb:** optimistic concurrency through the document ETag ([5555dfd](https://github.com/eQuantic/core-data/commit/5555dfd61c6b71cf0cbe2abd2f74af465aa6bda5))
* **mongodb:** reads join the active transaction session ([cde1957](https://github.com/eQuantic/core-data/commit/cde19576d5f0d29ae65162b48a71365f0302862c))
* **providers:** computed set-based updates via a shared update IR ([6b59be1](https://github.com/eQuantic/core-data/commit/6b59be1eeb35b9e5a155cbaec3f0d5e190094ca3))
* **providers:** IAsyncEnumerable streaming + MongoDB keyset continuation paging ([39faf6b](https://github.com/eQuantic/core-data/commit/39faf6b2f11dba6e41c8d09c1824d0de454cca98))

## [5.5.0](https://github.com/eQuantic/core-data/compare/v5.4.0...v5.5.0) (2026-07-21)

### Features

* **mongodb:** Include navigations via server-side $lookup ([455fef2](https://github.com/eQuantic/core-data/commit/455fef268bdbe1d7be07789b1a61ed2fbb39fb3b))

## [5.4.0](https://github.com/eQuantic/core-data/compare/v5.3.0...v5.4.0) (2026-07-21)

### Features

* **cassandra:** full provider on the reusable translator + CQL renderer ([58e2775](https://github.com/eQuantic/core-data/commit/58e2775db884852cb8bf0871d65537cfb91e9d26))
* **cassandra:** scaffold + table/key model + hybrid CQL filter translator ([da33ab0](https://github.com/eQuantic/core-data/commit/da33ab0ee61d9a4697053614b7a656dbcdc0e98c))
* **core:** reusable query-filter translation (IR + interpreter) ([b536831](https://github.com/eQuantic/core-data/commit/b536831a18a09733393fb5413ff544d7ce300061))

### Bug Fixes

* **cassandra:** materialize rows by Cassandra's lower-cased column names ([4905ade](https://github.com/eQuantic/core-data/commit/4905ade45d64fc4f0cad080b94b1947ca7926cd1))
* **cassandra:** migration history table cannot lead with an underscore ([500bc98](https://github.com/eQuantic/core-data/commit/500bc98c28c46dfbd4fcb8102bb60f7407e4eecb))

## [5.3.0](https://github.com/eQuantic/core-data/compare/v5.2.0...v5.3.0) (2026-07-20)

### Features

* **cosmosdb:** lean write model (UnitOfWork, Set) + $set→patch ([0205931](https://github.com/eQuantic/core-data/commit/02059311caa9d64eb55089287af1fc9e35dd44ce))
* **cosmosdb:** migration engine + DI ([3f2bd99](https://github.com/eQuantic/core-data/commit/3f2bd99d83325b6afa9ee45d8839035cb185562e))
* **cosmosdb:** partition-key inference + ToSelector via eQuantic.Linq.Expressions ([0073468](https://github.com/eQuantic/core-data/commit/007346836d1fdd45a0c431f523fd8434886fd69c))
* **cosmosdb:** repository read/write surface + query shaping ([7fb44f8](https://github.com/eQuantic/core-data/commit/7fb44f8664489b030778f1c25ed328d284d5dc9f))
* **cosmosdb:** scaffold the native provider + partition-key model ([8bc9431](https://github.com/eQuantic/core-data/commit/8bc9431b775aa1404e4160552a7c3cc674a5f0ec))

### Performance Improvements

* **cosmosdb:** server-side Sum via the SDK LINQ aggregate ([03dcc5b](https://github.com/eQuantic/core-data/commit/03dcc5bed8886afce484a3a4cd8a5cf277cdb550))

## [5.2.0](https://github.com/eQuantic/core-data/compare/v5.1.0...v5.2.0) (2026-07-20)

### Features

* fluent, typed migration authoring contracts ([b7da1d6](https://github.com/eQuantic/core-data/commit/b7da1d6da1d10b04c2d3918dd8e9306216229c1f))
* **migration:** add first-class migration abstractions to the contracts ([b602e66](https://github.com/eQuantic/core-data/commit/b602e663f60304b3ffbab75e72f7fc72fd4f25dc))
* MongoDB DI extensions + real-Mongo integration tests ([11157ea](https://github.com/eQuantic/core-data/commit/11157ea1f87d6483f22cb3cd466e4d7a62e11e69))
* MongoDB migration engine (executor, runner, history) ([2db1e33](https://github.com/eQuantic/core-data/commit/2db1e33ec9d76a89cebccb07383cba7034dd344a))
* **mongodb:** lean write model (staged writes + BulkWrite commit) + MongoSet ([6f05ca6](https://github.com/eQuantic/core-data/commit/6f05ca66046f18d6938571b5c0ad0628f575dd83))
* **mongodb:** scaffold the native provider + QueryOptions→IQueryable translator ([c8282b0](https://github.com/eQuantic/core-data/commit/c8282b0b98a4ad40aadaf1a40672063c7924f96d))
* native MongoDB repository read/write surface ([9f89d3c](https://github.com/eQuantic/core-data/commit/9f89d3c3b3c91bd896cf0755719bbbd49dc2e829))

## [5.1.0](https://github.com/eQuantic/core-data/compare/v5.0.0...v5.1.0) (2026-07-19)

### Features

* fluent typed filtering and ordering on QueryOptions ([e5bfb09](https://github.com/eQuantic/core-data/commit/e5bfb09207a0a7f7b5282cf8cc5be8ca1f9f16f7))
