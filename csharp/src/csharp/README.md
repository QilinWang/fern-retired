# Fern C#：正式运行和数据库位置由谁决定

Date: 2026-09-07
Updated: 2026-09-07T22:30:00-04:00
Author: GPT-6

## 修改代码前先找职责和工作流

远端文件检查、安装与执行首先看 [Fern.Site](Fern.Site/README.md)；
排队、授权和顺序才属于 Fern.Scheduler。不要因为入口在 Scheduler，
就在其中生成 shell 或 Python 去重写 Site 的文件规则。

改安装先读 [Plan installation](../../docs/how-to/plan-installation.md)，
改源码准备先读 [source freezing](../../docs/how-to/source-freezing.md)，
改进程生命周期先读 [command output](../../docs/how-to/command-output.md)。
[workflow 目录](../../docs/how-to/README.md)记录生产调用顺序；
修改前点名所属流程和调用者，修改时同步流程，验证时沿真实调用路径检查。

C# 负责正式实验的准入、Plan、调度、执行位置和运行事实。Python 编写并编译
科学 Study，也执行数值训练。先读
[运行与存储教程](../../docs/how-to/run-study-and-store-results.md)，再用命令。

**`run-study` 登记成功才有 Plan；进程在 GPU 上训练不能替代登记。**
`--admit-only` 只验证配置；成功也没有登记、没有启动训练。

在已选好 source-matched `FERN_RUN_BIN`、执行 profile 和正式 Home 后：

```bash
"$FERN_RUN_BIN" run-study --from /tmp/ettm1-compiled.json \
  --on runpod-ettm1-optimizer --admit-only

# PLAN_NAME 和 BINDING_ID 使用该实验实际批准、实际登记得到的值。
"$FERN_RUN_BIN" run-study --from /tmp/ettm1-compiled.json \
  --on runpod-ettm1-optimizer --plan "$PLAN_NAME" --binding "$BINDING_ID"

"$FERN_RUN_BIN" monitor --registry "$REGISTRY_HOME" --plan "$PLAN_NAME"
```

已有 Scheduler 消费登记后的工作；若服务未运行，执行已安装服务的启动流程。
不要从“登记成功”推断它已经训练。服务安装命令
`install-scheduler-service` 会输出实际服务名和启动命令，安装本身不启动工作。

`--execution auto|pod-only|local-only|hybrid` 选择可用机器；GPU 忙时留在原队列。
这些选项不授权另建 Registry，也不把配置拒绝转换成 Python 绕行。

## 看到这些拒绝时怎么处理

- **旧 host 不认识 knobs：**构建、验证、安装匹配的 C#；保留旧安装用于回退。
- **只有 entrypoint digest、没有 resolved implementation bundle：**当前实现
  身份不足，修源码证明的生成/接线。不能手造 bundle；不能把 `probe-implementation`
  当成仍存在的命令；不能改走未登记 Python 后宣称正式 Plan 已运行。
- **没有 Registry：**检查选定的 Home/profile 和已有运行声明。Worker 缺本地
  数据库不是新建 Home 的理由。新的正式独立实验库必须是用户明确选定的目的地。
- **冻结后缺 `serie.records` 等模块：**源码依赖遗漏。修完整清单并重新冻结；
  往已声明不可变的源码树零散补文件不能继续沿用旧 source identity。

[worker-preparation 工作流](../../docs/how-to/worker-preparation.md)实现 SDK
bootstrap、独立依赖环境和配置编译；完整 SSH 自动组合、环境路径消费和
frozen-source 控制集安装仍有未接环节，该文件明确区分了它们。

依赖方向和项目责任看 [repo_roadmap.md](../../repo_roadmap.md)。不要在 CLI 重写
Registry 查询、Python 数据集逻辑或另建 Scheduler；这里的命令包装对应库的操作。
