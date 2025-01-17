# ✨ FBus

[![Build status](https://github.com/pchalamet/fbus/workflows/build/badge.svg)](https://github.com/pchalamet/fbus/actions?query=workflow%3Abuild) 

FBus is a lightweight service-bus implementation written in F#.

It comes with default implementation for:
* Publish (broadcast), Send (direct) and Reply (direct)
* Conversation follow-up using headers (ConversationId and MessageId)
* RabbitMQ (with dead-letter support)
* Generic Host support with dependency injection
* System.Text.Json serialization
* Full testing capabilities using In-Memory mode
* Persistent queue/buffering across activation
* Sharding

Features that won't be implemented in FBus:
* Sagas: coordination is a big topic by itself - technically, everything required to handle this is available (ConversationId and MessageId). This can be handled outside of a service-bus.

# 📦 NuGet packages

Package | Status | Description
--------|--------|------------
FBus | [![Nuget](https://img.shields.io/nuget/v/FBus)](https://nuget.org/packages/FBus) | Core package
FBus.RabbitMQ | [![Nuget](https://img.shields.io/nuget/v/FBus.RabbitMQ)](https://nuget.org/packages/FBus.RabbitMQ) | RabbitMQ transport
FBus.Json | [![Nuget](https://img.shields.io/nuget/v/FBus.Json)](https://nuget.org/packages/FBus.Json) | System.Text.Json serializer
FBus.GenericHost | [![Nuget](https://img.shields.io/nuget/v/FBus.GenericHost)](https://nuget.org/packages/FBus.GenericHost) | Generic Host support
FBus.QuickStart | [![Nuget](https://img.shields.io/nuget/v/FBus.QuickStart)](https://nuget.org/packages/FBus.QuickStart) | All FBus packages to quick start a project

# 📚 Api

## Messages
In order to exchange messages using FBus, you have first to define messages. There are 2 types:
* events: messages that are broadcasted (see `Publish`)
* commands: messages that are sent to one client (see `Send` or `Reply`)

In order to avoid mistakes, messages are marked with a dummy interface:

For events:
```
type EventMessage =
    { msg: string }
    interface FBus.IMessageEvent
````

For commands:
```
type CommandMessage =
    { msg: string }
    interface FBus.IMessageCommand
```

For sharding scenarios, messages must also implement the `IMessageKey` interface:
```
type IMessageKey = 
    abstract Key: string with get
```
**NOTE:** the default routing key is an empty string.

For more information, see CQRS/Event Sourcing literature.

## Builder
Prior using the bus, a configuration must be built:

FBus.Builder | Description | Default
-------------|-------------|--------
`configure` | Start configuration with default parameters. |
`withName` | Change service name. Used to identify a bus client (see `IBusInitiator.Send` and `IBusConversation.Send`) | Name based on computer name, pid and random number.
`withShard` | Name the shard. Name must be provided to enable sharding. | None
`withTransport` | Transport to use. | None
`withContainer` | Container to use | None
`withSerializer` | Serializer to use | None
`withConsumer` | Add message consumer | None
`withHook` | Hook on consumer message processing | None
`withRecovery` | Connect to dead letter for recovery only | false
`build` | Returns a new bus instance (`FBus.IBusControl`) based on configuration | n/a

**NOTE:** bus clients are ephemeral by default - this is useful if you just want to connect to the bus for spying or sending commands for eg :-) Assigning a name (see `withName`) makes the client public so no queues are deleted upon exit.

## Bus
`IBusControl` is the primary interface to control the bus:

IBusControl | Description | Comments
------------|-------------|---------
`Start` | Start the bus. Returns `IBusInitiator` | Must be called before sending messages. Start accepts a resolve context which can be used by the container.
`Stop` | Stop the bus. | Bus can be restarted later on.
`Dispose` | Dispose the bus instance. | Bus can't be reused.

Once bus is started, `IBusInitiator` is available:

IBusInitiator | Description
--------------|------------
`Publish` | Broadcast an event message to all subscribers.
`Send` | Send a command message to given client.

**NOTE:** a new conversation is started when using this interface.

## Consumer
A consumer processes incoming messages: a context is provided (`IBusConversation`) and a message.

`IBusConversation` provides information to handlers and means to interact with the bus:

IBusConversation | Description
-----------------|------------
`Sender` | Name of the client.
`ConversationId` | Id of the conversation (identifier is flowing from initiator to subsequent consumers).
`MessageId` | Id the this message.
`Reply` | Provide a shortcut to reply to sender.
`Publish` | Broadcast an event message to all subscribers.
`Send` | Send a command message to given client.

**NOTE:** the current conversation is used when using this interface.

There are two kind of handlers:

### Class
Implement `IBusConsumer` interface on the class. Multiple implementation are allowed as of F# 5.0.
Use `withConsumer` to register the handlers.

```
type IBusConsumer<'t> =
    abstract Handle: IBusConversation -> 't -> unit
```
### Function
A function which enable partial application scenario: use `withFunConsumer`.
Use `withFunConsumer` to register the handler.

## InMemory
FBus provides InMemory implementation for transport, serializer and activator. They only exist to help testing or to easily prototype.

FBus.InMemory | Description | Comments
--------------|-------------|---------
`useTransport` | Register InMemory transport |
`useSerializer` | Register marshal by reference serializer | Object is preserved and passed by reference.
`useContainer` | Register default activator (see `System.Activator`) | Default constructor must exist.

**NOTE:** InMemory serializer does leak messages. This is by design.

## Testing
FBus can work in-memory, this is especially useful when unit-testing. Prior running a test in-memory, an `FBus.Testing.Session` instance has to be created. Multiple sessions can be created, they are completely isolated.

FBus.Testing.Session | Description | Comments
---------------------|-------------|---------
`Use` | Configure FBus for unit-testing | Configure transport, serializer and activator.
`WaitForCompletion` | Wait for all messages to be processed | This method blocks until completion.
`ClearCache` | Clear InMemory serializer cache | Shall not be used unless necessary.

## Thread safety
FBus is thread safe and all existing extensions in this repository.

# 🛠️ Extensibility
Following extension points are supported:
* Transports: which middleware is transporting messages.
* Serializers: how messages are exchanged on the wire.
* Containers: where and how consumers are allocated and hosted.
* Hooks: handlers in case of failures.

## Messages
There are 2 types of messages:
* events: messages that are broadcasted (see `Publish`)
* commands: messages that are sent to one client (understand `Send`)

In order to avoid mistakes, messages are marked with a dummy interface.

For events:
```
type EventMessage =
    { msg: string }
    interface FBus.IMessageEvent
````

For commands:
```
type CommandMessage =
    { msg: string }
    interface FBus.IMessageCommand
```

## Transports
Two transports are available out of the box: RabbitMQ and InMemory. Still, it's possible to easily add new middlewares.

See `FBus.IBusTransport`.

## Containers
Containers are in charge of activating and running consumers.

See `FBus.IBusContainer`.

## Serializers
Serializers transform objects into byte streams and vis-versa without relying on native middleware capabilities.

See `FBus.IBusSerializer`.

## Consumers
Consumers can be configured at will. There is one major restriction: only one handler per type is supported. If you want several subscribers, you will have to handle delegation.

See `FBus.IBusConsumer<>`.

## Hooks
Allow one to observe errors while processing messages.

See `FBus.IBusHook`.

FBus.IBusHook | Description | Comments
--------------|-------------|---------
`OnStart` | Invoked after the bus is started - use to start additional services if required |
`OnStop` | Invoked before the bus is stopped - use to stop additional services if required |
`OnBeforeProcessing` | Invoked before processing a message | Must not throw - can return an IDisposable object released once processing is done.
`OnError` | Invoked on error | Must not throw.

## Available extensions

### RabbitMQ (package FBus.RabbitMQ)

**NOTE**: In order to use sharding with this transport, you **must** install plugin `rabbitmq_consistent_hash_exchange`.

FBus.RabbitMQ | Description | Comments
--------------|-------------|---------
`useDefaults` | Configure RabbitMQ as transport | Endpoint is set to `amqp://guest:guest@localhost`.
`useWith` | Configure RabbitMQ as transport with provided URI |

Transport leverages exchanges (one for each message type) to distribute messages across consumers (subscribing a queue).

It supports only a simple concurrency model:
* no concurrency at bus level for receive. This does not mean you can't have concurrency, you just have to handle it explicitely: you have to create multiple bus instances in-process and it's up to you to synchronize correctly among threads if required.
* Sending is a thread safe operation - but locking happens behind the scene to access underlying channel/connection.
* Automatic recovery is configured on connection.

The default implementation use following settings:
* messages are sent as persistent
* a consumer fetches one message at a time and ack/nack accordingly
* message goes to dead-letter on error
* prefetch size is 0
* prefetch count is 10

### Json (package FBus.Json)

FBus.Json | Description | Comments
----------|-------------|---------
`useDefaults` | Configure System.Text.Json as serializer | FSharp.SystemTextJson](https://github.com/Tarmil/FSharp.SystemTextJson) is used to deal with F# types.
`useWith` | Same as `useDefaults` but with provided configuration options |

### QuickStart (package FBus.QuickStart)

FBus.QuickStart | Description | Comments
`configure` | Configure FBus with RabbitMQ, Json and In-Memory Activator. |

### GenericHost

FBus.GenericHost | Description | Comments
-----------------|-------------|---------
`AddFBus` | Inject FBus in GenericHost container | `FBus.IBusControl` and `FBus.IBusInitiator` are available in injection context. 

# ⚽ Samples

## In-Process console
### Client
```
open FBus
open FBus.Builder

use bus = FBus.QuickStart.configure() |> build

let busInitiator = bus.Start()
busInitiator.Send "hello from FBus !"
```

### Server
```
open FBus
open FBus.Builder

type MessageConsumer() =
    interface IConsumer<Message> with
        member this.Handle context msg = 
            printfn "Received message: %A" msg

use bus = FBus.QuickStart.configure() |> withConsumer<MessageConsumer> 
                                      |> build
bus.Start() |> ignore
```

## Server (generic host)
```
...
let configureBus builder =
    builder |> withName "server"
            |> withConsumer<MessageConsumer>
            |> Json.useDefaults
            |> RabbitMQ.useDefaults

Host.CreateDefaultBuilder(argv)
    .ConfigureServices(fun services -> services.AddFBus(configureBus) |> ignore)
    .UseConsoleLifetime()
    .Build()
    .Run()
```

# 🏭 Build it
A makefile is available:
* `make [build]`: build FBus
* `make test`: build and test FBus

If you prefer to build using your IDE, solution file is named `fbus.sln`.

# 📜 Licensing
The project is licensed under MIT.

For more information on the license see the [license file](./LICENSE).
