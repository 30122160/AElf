﻿using System.Threading.Tasks;
using AElf.Kernel.Storages;
using AElf.Kernel.Types;

namespace AElf.Kernel.Managers
{
    public class TransactionResultManager : ITransactionResultManager
    {
        private readonly ITransactionResultStore _transactionResultStore;

        public TransactionResultManager(ITransactionResultStore transactionResultStore)
        {
            _transactionResultStore = transactionResultStore;
        }

        public async Task AddTransactionResultAsync(TransactionResult tr)
        {
            var trKey = ResourcePath.CalculatePointerForTxResult(tr.TransactionId);
            await _transactionResultStore.InsertAsync(trKey, tr);
        }

        public async Task<TransactionResult> GetTransactionResultAsync(Hash txId)
        {
            var trKey = ResourcePath.CalculatePointerForTxResult(txId);
            return await _transactionResultStore.GetAsync(trKey);
        }
    }
}