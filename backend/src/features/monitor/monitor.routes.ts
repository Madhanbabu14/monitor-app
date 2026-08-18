import { Router } from 'express';
import { monitorController } from './monitor.controller';
import { authenticate } from '../../middleware/auth.middleware';

const router = Router();
router.use(authenticate);

router.get('/dashboard',            monitorController.getDashboard.bind(monitorController));
router.get('/not-processed-count',  monitorController.getNotProcessedCount.bind(monitorController));
router.get('/reconcile',   monitorController.getReconciled.bind(monitorController));
router.get('/files',       monitorController.getFiles.bind(monitorController));
router.get('/pipeline-names', monitorController.getPipelineNames.bind(monitorController));
router.get('/db-status', monitorController.getDbStatus.bind(monitorController));

export { router as monitorRoutes };
