import { Router } from 'express';
import { s3Controller } from './s3.controller';
import { authenticate } from '../../middleware/auth.middleware';

const router = Router();
router.use(authenticate);

router.get('/files', s3Controller.listFiles.bind(s3Controller));
router.get('/pipeline-names', s3Controller.pipelineNames.bind(s3Controller));
router.get('/download', s3Controller.downloadFile.bind(s3Controller));
router.post('/retrigger', s3Controller.retriggerFile.bind(s3Controller));

export { router as s3Routes };
