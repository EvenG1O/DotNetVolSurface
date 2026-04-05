# iv-surface-engine

ASP.NET Core backend that pulls live BTC/ETH options data from Deribit via WebSocket and builds an Implied Volatility Surface.

## What This Is

This is the backend engine. It:
- Establishes a real-time WebSocket connection to Deribit
- Filters to liquid strikes
- Streams structured IV surface data (Expiry × Strike → IV) to connected clients over WebSocket

## Frontend & Live Demo

- **Live Website**: [https://EvenG1O.github.io/IvSurfaceUi/](https://EvenG1O.github.io/IvSurfaceUi/)
- **Frontend Repository**: [https://github.com/EvenG1O/IvSurfaceUi](https://github.com/EvenG1O/IvSurfaceUi)
