## Data Flow

**Read path:** `REST API → Controller → Handler → ResourceResolver<T> → Cache hit? return : Aggregator → ECE/Discovery/Download → Cache & return`

**Write path:** `REST API → Controller → Handler → Manager → Reliable Collections + ECE action plan → CacheInvalidator events → 202 Accepted`

**Discovery (background, hourly):** Discovers updates from Component/OEM/Local providers via signed XML manifests, validates signatures, stores packages, raises cache refresh events.