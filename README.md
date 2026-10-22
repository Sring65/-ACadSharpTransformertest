# dwgReadWrite — ACadSharp DWG 读写测试工具

ACadSharp（`port/v3.8.27` 分支，基于上游 v3.8.27）配套的 DWG/DXF 读写、变换、
增量保存测试工具。

## 构建

- 要求：.NET 10 SDK
- `dwgReadWrite.csproj` 中的 `ProjectReference` 是**绝对路径**，指向本地
  `D:/soft/ACadSharpTransformertest/ACadSharp/src/ACadSharp/ACadSharp.csproj`。
  在其他机器上构建前，请修改该路径为你本地 ACadSharp 仓库的位置
  （建议改为相对路径，如 `../../ACadSharp/src/ACadSharp/ACadSharp.csproj`）。
- 构建：`dotnet build -c Release`

## 测试数据

| 文件 | 说明 |
|---|---|
| `sample.dwg` | 主测试文件（约 70 个实体） |
| `Aaa.shx` / `shape.shx` | SHX shape 字体（SHAPE 实体测试） |
| `sample.pdf` | PDF 底图测试文件 |
| `image.jpg` | 底图/图像测试文件 |

## 运行

```
dotnet bin/Release/net10.0/dwgReadWrite.dll [子命令...]
```

- **不带参数**：默认流程 —— 对 `sample.dwg` 做坐标变换 + 全版本
  （AC1014–AC1032）DWG 保存 + 全量克隆 + 逐类克隆，并打印结果报告。
- **子命令**（缺参数时各自打印用法）：`empty <版本> [out.dwg]`、
  `rt <in.dwg> <out.dwg> [版本]`、`mktypes <out.dwg>`、`stripvp`、
  `dump <file.dwg>`、`classes <file.dwg>`、`objinfo`、`layoutdump`、
  `dimstyles <file.dwg>`、`inspect`、`inspecthatch`、`inspectdim`、
  `clonedump <file.dwg> [--clone]`、`clonert <in.dwg> <out.dwg>` 等。
