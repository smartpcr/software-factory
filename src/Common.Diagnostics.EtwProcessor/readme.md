## Usage

### 1. scan - Scan and report statistics

EtwProcessor scan -i <input-directory>
Reports:
- All providers found
- Start/end time per provider
- Event counts per provider


```
EtwProcessor scan -i E:\icm\salt
```

### 2. parse - Parse to CSV by provider

EtwProcessor parse -i <input-directory> -o <output-directory>
- Extracts all events to CSV
- Organizes output by provider subdirectories:
  output/
  Microsoft-Windows-Kernel/
  EventName1.csv
  EventName2.csv
  AnotherProvider/

```
EtwProcessor parse -i E:\icm\salt -o C:\parsed
```


### 3. ingest - Ingest CSVs to Kusto

EtwProcessor ingest -i <parsed-directory> -c <cluster-uri> -d <database>
- Reads provider directories from parse output
- Creates tables automatically from CSV headers
- Ingests with proper CSV mappings

```
EtwProcessor ingest -i C:\parsed -c https://mycluster.kusto.windows.net -d MyDatabase
```