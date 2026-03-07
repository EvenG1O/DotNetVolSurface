# iv-surface-engine

ASP.NET Core API that pulls live BTC/ETH options data from Deribit and builds an Implied Volatility Surface.

## What This Is

This is the backend engine. It:
- Fetches live option instruments and order book data from Deribit's public API
- Filters to liquid strikes 
- Builds a structured IV surface (Expiry × Strike → IV)

