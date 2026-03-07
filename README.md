# iv-surface-engine

ASP.NET Core API that pulls live BTC/ETH options data from Deribit and builds an Implied Volatility Surface.

## What This Is

This is the backend engine. It:
- Fetches live option instruments and order book data from Deribit's public API
- Filters to liquid strikes (±15% ATM, OTM only, nearest 6 expiries)
- Builds a structured IV surface (Expiry × Strike → IV)
- Caches the result for 5 minutes to respect Deribit's rate limits
- Exposes a single REST endpoint consumed by the frontend
