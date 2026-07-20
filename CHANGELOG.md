# Changelog

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
