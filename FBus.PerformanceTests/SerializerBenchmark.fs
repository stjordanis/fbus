namespace FBus.PerformanceTests.Serializer
open FBus
open BenchmarkDotNet.Attributes
open System


type InMemoryMessage =
    { Content1: string
      Value1: int
      Value2: float } 
    interface IMessageCommand

type InMemoryHandler() =
    interface IBusConsumer<InMemoryMessage> with
        member _.Handle ctx msg = 
            ()

[<IterationCount(5_000)>]
type SerializerBenchmark() =
    let mutable bus: IBusControl = Unchecked.defaultof<IBusControl>
    let mutable busInitiator: IBusInitiator = Unchecked.defaultof<IBusInitiator>
    let msg = { Content1 = "toto"
                Value1 = 42
                Value2 = 66.6 }

    [<Params("InMemory", "Json")>]
    member val SerializerKind = "InMemory" with get, set

    member this.InitSerializer busBuilder =
        let useSerializer = match this.SerializerKind with
                            | "Json" -> Json.useSerializer
                            | _ -> InMemory.useSerializer
        useSerializer busBuilder

    [<GlobalSetup>]
    member this.GlobalSetup() =
        let busBuilder = Builder.init() |> InMemory.useTransport
                                        |> InMemory.useContainer
                                        |> Builder.withConsumer<InMemoryHandler>
                                        |> this.InitSerializer

        bus <- busBuilder |> Builder.build
        busInitiator <- bus.Start()

    [<GlobalCleanup>]
    member _.GlobalCleanup() =
        bus.Stop()
        bus.Dispose()

    [<IterationCleanup>]
    member _.IterationCleanUp() =
        InMemory.waitForCompletion()

    [<Benchmark>]
    member _.Publish () =
        busInitiator.Publish msg