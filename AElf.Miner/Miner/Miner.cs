﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AElf.ChainController;
using AElf.Common.Attributes;
using AElf.Cryptography.ECDSA;
using AElf.Execution;
using AElf.Execution.Scheduling;
using AElf.Kernel;
using AElf.Kernel.Managers;
using AElf.SmartContract;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using NLog;
using ITxPoolService = AElf.ChainController.TxMemPool.ITxPoolService;
using ReaderWriterLock = AElf.Common.Synchronisation.ReaderWriterLock;
using Status = AElf.Kernel.Status;

namespace AElf.Miner.Miner
{
    [LoggerName("Miner")]
    public class Miner : IMiner
    {
        private readonly ITxPoolService _txPoolService;
        private ECKeyPair _keyPair;
        private readonly IChainService _chainService;
        private readonly IWorldStateDictator _worldStateDictator;
        private readonly ISmartContractService _smartContractService;
        private readonly IExecutingService _executingService;
        private readonly ITransactionManager _transactionManager;
        private readonly ITransactionResultManager _transactionResultManager;

        private MinerLock Lock { get; } = new MinerLock();
        private readonly ILogger _logger;
        
        
        /// <summary>
        /// Signals to a CancellationToken that mining should be canceled
        /// </summary>
        public CancellationTokenSource Cts { get; private set; }

        public IMinerConfig Config { get; }

        public Hash Coinbase => Config.CoinBase;

        public Miner(IMinerConfig config, ITxPoolService txPoolService,  IChainService chainService, 
            IWorldStateDictator worldStateDictator,  ISmartContractService smartContractService, 
            IExecutingService executingService, ITransactionManager transactionManager, 
            ITransactionResultManager transactionResultManager, ILogger logger)
        {
            Config = config;
            _txPoolService = txPoolService;
            _chainService = chainService;
            _worldStateDictator = worldStateDictator;
            _smartContractService = smartContractService;
            _executingService = executingService;
            _transactionManager = transactionManager;
            _transactionResultManager = transactionResultManager;
            _logger = logger;
        }

        
        public async Task<IBlock> Mine()
        {
            try
            {
                if (Cts == null || Cts.IsCancellationRequested)
                    return null;            

                var readyTxs = await _txPoolService.GetReadyTxsAsync();
                // TODO：dispatch txs with ISParallel, return list of tx results

                // reset Promotable and update account context
                
                _logger?.Log(LogLevel.Debug, "Executing Transactions..");
                List<TransactionTrace> traces = null;
                var blockChain = _chainService.GetBlockChain(Config.ChainId);
                if(Config.IsParallel)
                {  
                    traces = readyTxs.Count == 0
                    ? new List<TransactionTrace>()
                    : await _executingService.ExecuteAsync(readyTxs, Config.ChainId, Cts.Token);
                }
                else
                {
                    foreach (var transaction in readyTxs)
                    {
                        var executive = await _smartContractService.GetExecutiveAsync(transaction.To, Config.ChainId);
                        try
                        {
                            var txnInitCtxt = new TransactionContext()
                            {
                                Transaction = transaction,
                                BlockHeight = await blockChain.GetCurrentBlockHeightAsync()
                            };
                            _worldStateDictator.PreBlockHash = await _chainService.GetBlockChain(Config.ChainId).GetCurrentBlockHashAsync();
                            await executive.SetTransactionContext(txnInitCtxt).Apply(true);
                        }
                        finally
                        {
                            await _smartContractService.PutExecutiveAsync(transaction.To, executive);    
                        }

                    }
                }
                _logger?.Log(LogLevel.Debug, "End Executing Transactions..");
                var results = new List<TransactionResult>();
                foreach (var trace in traces)
                {
                    var res = new TransactionResult()
                    {
                        TransactionId = trace.TransactionId
                    };
                    if (string.IsNullOrEmpty(trace.StdErr))
                    {
                        res.Logs.AddRange(trace.FlattenedLogs);
                        res.Status = Status.Mined;
                        res.RetVal = ByteString.CopyFrom(trace.RetVal.ToFriendlyBytes());
                        res.UpdateBloom();
                    }
                    else
                    {
                        res.Status = Status.Failed;
                        res.RetVal = ByteString.CopyFromUtf8(trace.StdErr);
                        Console.WriteLine("Failed to execute tx:\n" + trace.StdErr);
                    }
                    results.Add(res);
                }
                
                // insert txs to db
                // update tx pool state
                var addrs = await InsertTxs(readyTxs, results);
                await _txPoolService.UpdateAccountContext(addrs);
            
                _logger?.Log(LogLevel.Debug, "Generating block..");
                // generate block
                var block = await GenerateBlockAsync(Config.ChainId, results);

                block.Header.Bloom =ByteString.CopyFrom( 
                    Bloom.AndMultipleBloomBytes(
                        results.Where(x=>!x.Bloom.IsEmpty).Select(x=>x.Bloom.ToByteArray())
                    )
                );
                _logger?.Log(LogLevel.Debug, "Generating block End..");

                // sign block
                ECSigner signer = new ECSigner();
                var hash = block.GetHash();
                var bytes = hash.GetHashBytes();
                ECSignature signature = signer.Sign(_keyPair, bytes);

                block.Header.P = ByteString.CopyFrom(_keyPair.PublicKey.Q.GetEncoded());
                block.Header.R = ByteString.CopyFrom(signature.R);
                block.Header.S = ByteString.CopyFrom(signature.S);

                // append block
                await blockChain.AddBlocksAsync(new List<IBlock>(){ block });
  
                return block;
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                return null;
            }
        }
        
        /// <summary>
        /// update database
        /// </summary>
        /// <param name="executedTxs"></param>
        /// <param name="txResults"></param>
        private async Task<HashSet<Hash>> InsertTxs(List<ITransaction> executedTxs, List<TransactionResult> txResults)
        {
            var addrs = new HashSet<Hash>();
            foreach (var t in executedTxs)
            {
                addrs.Add(t.From);
                await _transactionManager.AddTransactionAsync(t);
            }
            
            txResults.ForEach(async r =>
            {
                await _transactionResultManager.AddTransactionResultAsync(r);
            });
            return addrs;
        }
        
        /// <summary>
        /// generate block
        /// </summary>
        /// <param name="chainId"></param>
        /// <param name="results"></param>
        /// <returns></returns>
        public async Task<IBlock> GenerateBlockAsync(Hash chainId, IEnumerable<TransactionResult> results)
        {
            var blockChain = _chainService.GetBlockChain(chainId);
            
            var currentBlockHash = await blockChain.GetCurrentBlockHashAsync();
            var index = await blockChain.GetCurrentBlockHeightAsync() + 1;
            var block = new Block(currentBlockHash)
            {
                Header =
                {
                    Index = index,
                    ChainId = chainId
                }
            };

            // add tx hash
            foreach (var r in results)
            {
                block.AddTransaction(r.TransactionId);
            }
        
            _logger?.Log(LogLevel.Debug, "Calculating MK Tree Root..");
            // calculate and set tx merkle tree root
            block.FillTxsMerkleTreeRootInHeader();
            _logger?.Log(LogLevel.Debug, "Calculating MK Tree Root End..");

            
            // set ws merkle tree root
            await _worldStateDictator.SetWorldStateAsync(currentBlockHash);
            _logger?.Log(LogLevel.Debug, "End Set WS..");
            var ws = await _worldStateDictator.GetWorldStateAsync(currentBlockHash);
            _logger?.Log(LogLevel.Debug, "End Get Ws");
            block.Header.Time = Timestamp.FromDateTime(DateTime.UtcNow);


            if (ws != null)
            {
                block.Header.MerkleTreeRootOfWorldState = await ws.GetWorldStateMerkleTreeRootAsync();
                _logger?.Log(LogLevel.Debug, "End GetWorldStateMerkleTreeRootAsync");
            }
               
            block.Body.BlockHeader = block.Header.GetHash();
            return block;
        }
        
        
        /// <summary>
        /// generate block header
        /// </summary>
        /// <param name="chainId"></param>
        /// <param name="merkleTreeRootForTransaction"></param>
        /// <returns></returns>
        public async Task<IBlockHeader> GenerateBlockHeaderAsync(Hash chainId, Hash merkleTreeRootForTransaction)
        {
            // get ws merkle tree root
            var blockChain = _chainService.GetBlockChain(chainId);
            
            var lastBlockHash = await blockChain.GetCurrentBlockHashAsync();
            // TODO: Generic IBlockHeader
            var lastHeader = (BlockHeader) await blockChain.GetHeaderByHashAsync(lastBlockHash);
            var index = lastHeader.Index;
            var block = new Block(lastBlockHash);
            block.Header.Index = index + 1;
            block.Header.ChainId = chainId;
            
            
            var ws = await _worldStateDictator.GetWorldStateAsync(lastBlockHash);
            var state = await ws.GetWorldStateMerkleTreeRootAsync();
            
            var header = new BlockHeader
            {
                Version = 0,
                PreviousBlockHash = lastBlockHash,
                MerkleTreeRootOfWorldState = state,
                MerkleTreeRootOfTransactions = merkleTreeRootForTransaction
            };

            return header;

        }

        
        
        /// <summary>
        /// start mining  
        /// </summary>
        public void Start(ECKeyPair nodeKeyPair)
        {
            if (Cts == null || Cts.IsCancellationRequested)
            {
                Cts = new CancellationTokenSource();
                _keyPair = nodeKeyPair;
                // init miner rpc server
            }
        }

        /// <summary>
        /// stop mining
        /// </summary>
        public void Stop()
        {
            Lock.WriteLock(() =>
            {
                Cts.Cancel();
                Cts.Dispose();
                _keyPair = null;
                //MiningResetEvent.Dispose();
            });
            
        }

    }
    
    /// <inheritdoc />
    /// <summary>
    /// A lock for managing asynchronous access to memory pool.
    /// </summary>
    public class MinerLock : ReaderWriterLock
    {
    }
}