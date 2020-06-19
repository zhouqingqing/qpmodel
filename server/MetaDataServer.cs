using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime;
using System.Text;
using System.Threading;
using System.Net.Sockets;
using System.Net;
using System.Reflection;

namespace server
{
    abstract class BackendServer
    {
        public abstract void Start();

        public void SendMsg(NetworkStream stream, string msg)
        {
            Byte[] sendBytes = Encoding.Default.GetBytes(msg);
            stream.Write(sendBytes, 0, sendBytes.Length);
            stream.Flush();
            Console.WriteLine($"{this.GetType().Name}: send {msg}");
        }

        public string RecvMsg(NetworkStream stream)
        {
            byte[] bytesFrom = new byte[64*1024];
            stream.Read(bytesFrom, 0, 64*1024);
            var msg = System.Text.Encoding.Default.GetString(bytesFrom, 0, Array.FindLastIndex(bytesFrom, b => b != 0) + 1);
            Console.WriteLine($"{this.GetType().Name}: recv {msg}");
            return msg;
        }

        public string SendAndRecvMsg(NetworkStream stream, string msg)
        {
            SendMsg(stream, msg);
            return RecvMsg(stream);
        }
    }

    // Here we emulate a distributed meta data server (MD) and a bunch of query processing
    // servers (QP) where:
    //  - MD holds the single truth about meta data
    //  - QP needs to cache meta data for local faster read
    // They shall work together to keep meta data r/w consistently. For example, if QP 
    // increased column length to 5, then all QPs shall not be able to insert data with
    // column length larger than 5. 
    //
    // To simplify modeling, Meta data server only maintain column's length. Client 
    // will can only do two transactions:
    //   T1: (ALTER) alter column length, reduce or increase. Reduction will need QPs verify
    //   they are good with reduction.
    //   T2. (UPDATE) update column values with random column length.
    // 
    // QP will generate T1 and T2 randomly and consistency are checked.
    //
    class MetaDataServer: BackendServer
    {
        public const int Port_ = 8888;
        public const string Host_ = "127.0.0.1";
        TcpListener serverSocket_ = new TcpListener(IPAddress.Parse(Host_), Port_);

        readonly Dictionary<string, int> columns_ = new Dictionary<string, int>();

        override public void Start() 
        {
            // populate meta data store
            columns_.Add("c0", 10);
            columns_.Add("c1", 10);
            columns_.Add("c2", 10);
            columns_.Add("c3", 10);

            serverSocket_.Start();
            Console.WriteLine("MD Server Started");

            while (!Test.stopit_)
            {
                var clientSocket = serverSocket_.AcceptTcpClient();
                Console.WriteLine("Accept connection from client");

                // start a thread to handle the request async
                Thread thread = new Thread(() =>
                {
                    while(!Test.stopit_)
                    {
                        NetworkStream stream = clientSocket.GetStream();
                        var command = RecvMsg(stream);
                        HandleQPCommand(stream, command);
                    }

                    // close connection
                    clientSocket.Close();
                });
                thread.Start();
            }

            serverSocket_.Stop();
            Console.WriteLine("MD Server exit");
            Console.ReadLine();
        }

        public void AlterColumnLength(NetworkStream stream, string column, int newlen)
        {
            bool success = true;
            int curlen = columns_[column];
            if (curlen < newlen)
            {
                columns_[column] = newlen;

                // debug only
                var answer = SendAndRecvMsg(stream, $"VERIFY {column} {newlen}");
                Debug.Assert(answer.Equals("True"));
            }
            else
            {
                // QPs have to verify they are good with column length reduction
                var answer = SendAndRecvMsg(stream, $"CHECK {column} {newlen}");
                if (answer.Equals("True"))
                    columns_[column] = newlen;
                else
                {
                    Debug.Assert(answer.Equals("False"));
                    success = false;
                }
            }

            SendMsg(stream, success ? "COMMITTED": "ABORTED");
        }

        // Commands:
        //   ALTER <column> <len>
        //   GELEN <column>
        //
        void HandleQPCommand(NetworkStream stream, string command)
        {
            string[] words = command.Split(' ');
            switch (words[0])
            {
                case "ALTER":
                    AlterColumnLength(stream, words[1], int.Parse(words[2]));
                    break;
                case "GETLEN":
                    SendMsg(stream, columns_[words[1]].ToString());
                    break;
                default:
                    throw new InvalidProgramException();
            }
        }
    }

    class QueryServer: BackendServer
    {
        // Transaction syntax
        //    ALTER <column name> <new length>
        //    UPDATE <cloumn name> <length>
        //
        class Heap 
        {
            public List<int> valLength_ = new List<int>();

            public Heap() => Populate(10);

            void Populate(int nrows = 10)
            {
                for (int i = 0; i < nrows; i++)
                    valLength_.Add(1);
            }

            internal void VerifyLength(int len)
            {
                foreach (var v in valLength_)
                   Debug.Assert(v <= len);
            }

            internal bool CheckLength(int len)
            {
                foreach (var v in valLength_)
                    if (v > len)
                        return false;
                return true;
            }

            internal void UpdateWithColumnLengthTo(int len)
            {
                var rand = new Random();
                for (int i = 0; i < valLength_.Count; i++)
                {
                    var newlen = rand.Next() % len + 1;
                    valLength_[i] = newlen;
                }
                VerifyLength(len);
            }
        }
        Dictionary<string, Heap> tables_ = new Dictionary<string, Heap>();

        class Profile {
            internal ulong nAlters_ = 0;
            internal ulong nUpdates_ = 0;

            public override string ToString()
            {
                return $"ALTER: {nAlters_}, UPDATE: {nUpdates_}";
            }
        }
        Profile profile_ = new Profile();
        TcpClient socket_ = new TcpClient();

        void VerifyLengthComplaince(string column, int len)
        {
            var heap = tables_[column];
            heap.VerifyLength(len);
        }

        bool RequestMDToAlterColumn(string column, int newlen)
        {
            var success = true;
            var stream = socket_.GetStream();
            var answer = SendAndRecvMsg(stream, $"ALTER {column} {newlen}");

        restart:
            string[] words = answer.Split(' ');
            switch (words[0])
            {
                case "COMMITTED":
                    break;
                case "ABORTED":
                    success = false;
                    break;
                case "CHECK":
                case "VERIFY":
                    var ok = true;
                    int len = int.Parse(words[2]);
                    if (words[0].Equals("VERIFY"))
                        tables_[words[1]].VerifyLength(len);
                    else
                        ok = tables_[words[1]].CheckLength(len);
                    answer = SendAndRecvMsg(stream, $"{ok}");
                    goto restart;
                default:
                    throw new InvalidProgramException();
            }
            
            return success;
        }

        public int TransactionAlterLength(string column, int newlen)
        {
            // request meta data server to alter column
            if (!RequestMDToAlterColumn(column, newlen))
                return -1;

            // update local cache: handle MVCC
            
            // release locks and leaving the transaction
            return 0;
        }

        public int TransactionUpdate(string column)
        {
            var rand = new Random();
            var answer = SendAndRecvMsg(socket_.GetStream(), $"GETLEN {column}");
            var len = int.Parse(answer);
            var heap = tables_[column];
            heap.UpdateWithColumnLengthTo(rand.Next() % len + 1);
            return 0;
        }

        void PopulateDatabase()
        {
            tables_.Add("c0", new Heap());
            tables_.Add("c1", new Heap());
            tables_.Add("c2", new Heap());
            tables_.Add("c3", new Heap());
        }

        void ConnectToMDServer()
        {
            socket_.Connect(MetaDataServer.Host_, MetaDataServer.Port_);
        }

        override public void Start()
        {
            PopulateDatabase();
            ConnectToMDServer();

            var rand = new Random();
            while (!Test.stopit_)
            {
                var value = rand.Next() % 2;
                var col = rand.Next() % 4;
                switch (value)
                {
                    case 0:
                        var newlen = rand.Next() % 50;
                        var ret = TransactionAlterLength($"c{col}", newlen);
                        profile_.nAlters_++;
                        break;
                    case 1:
                        TransactionUpdate($"c{col}");
                        profile_.nUpdates_++;
                        break;
                    default:
                        throw new InvalidProgramException();
                }
            }

            // print stats
            Console.WriteLine($"thread: {Thread.CurrentThread}: {profile_}");
        }
    }

    class Test 
    {
        public static bool stopit_ { get; set; } = false;

        static void MetaServerStart()
        {
            var md = new MetaDataServer();
            md.Start();
        }

        static void QueryServerStart()
        {
            var qp = new QueryServer();
            qp.Start();
        }

        public static void Run(int nQPs)
        {
            var mdserver = new Thread(new ThreadStart(MetaServerStart));
            mdserver.Start();
            Thread.Sleep(1*1000);

            // current there is no concurrency control
            Debug.Assert(nQPs == 1);

            List<Thread> qpservers = new List<Thread>();
            for (int i = 0; i < nQPs; i++)
            {
                var qp = new Thread(new ThreadStart(QueryServerStart));
                qpservers.Add(qp);
            }
            Console.WriteLine($"started {nQPs} Query Servers");
            qpservers.ForEach(x => x.Start());

            // press any key stops the running
            Console.ReadKey();
            stopit_ = true;
        }
    }
}
