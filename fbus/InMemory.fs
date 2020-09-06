module FBus.InMemory
open FBus
open System.Collections.Concurrent
open System



type Serializer() =
    static let refs = ConcurrentDictionary<Guid, obj>()

    interface IBusSerializer with
        member _.Serialize (v: obj) =
            let id = Guid.NewGuid()
            let body = id.ToByteArray() |> ReadOnlyMemory
            if refs.TryAdd(id, v) |> not then failwith "Failed to store object"
            body

        member _.Deserialize (t: System.Type) (body: ReadOnlyMemory<byte>) =
            let id = Guid(body.ToArray())
            match refs.TryRemove(id) with
            | true, v -> v
            | _ -> failwith "Failed to retrieve object"


type private ProcessingAgentMessage =
    | Message of (Map<string, string> * string * System.ReadOnlyMemory<byte>)
    | Exit

type Transport(busBuilder, msgCallback) =
    static let initLock = obj()
    static let mutable transports = Map.empty
    static let mutable msgInFlight = 0
    static let doneHandle = new System.Threading.ManualResetEvent(false)

    static let newMsgInFlight() =
        let msgInFlight = System.Threading.Interlocked.Increment(&msgInFlight)
        if msgInFlight = 1 then doneHandle.Reset() |> ignore

    static let doneMsgInFlight() =
        let msgInFlight = System.Threading.Interlocked.Decrement(&msgInFlight)
        if msgInFlight = 0 then doneHandle.Set() |> ignore

    let processingAgent = MailboxProcessor.Start (fun inbox ->
        let rec messageLoop() = async {
            let! msg = inbox.Receive()
            match msg with
            | Message (headers, msgType, body) -> msgCallback headers msgType body
                                                  doneMsgInFlight()
                                                  return! messageLoop() 
            | Exit -> ()
        }

        messageLoop()
    )

    static member Create (busBuilder: BusBuilder) msgCallback =
        doneHandle.Reset() |> ignore
        let transport = new Transport(busBuilder, msgCallback)
        lock initLock (fun () -> transports <- transports |> Map.add busBuilder.Name transport)
        transport :> IBusTransport

    static member WaitForCompletion() =
        doneHandle.WaitOne() |> ignore

    member private _.Accept msgType =
        busBuilder.Handlers |> Map.containsKey msgType

    member private _.Dispatch headers msgType body =
        (headers, msgType, body) |> Message |> processingAgent.Post 

    interface IBusTransport with
        member _.Publish headers msgType body =
            transports |> Map.filter (fun _ transport -> transport.Accept msgType)
                       |> Map.iter (fun _ transport -> newMsgInFlight()
                                                       transport.Dispatch headers msgType body)

        member _.Send headers client msgType body =
            transports |> Map.tryFind client 
                       |> Option.iter (fun transport -> newMsgInFlight()
                                                        transport.Dispatch headers msgType body)

        member _.Dispose() =
            lock initLock (fun () -> transports <- transports |> Map.remove busBuilder.Name)
            Exit |> processingAgent.Post
